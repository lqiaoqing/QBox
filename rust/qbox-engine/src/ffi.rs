//! C ABI sidecar：GUI/CLI 通过 LibraryImport 在未提权进程内调用。

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::slice;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex, RwLock};

use crate::cancel::CancelToken;
use crate::history::parse_history_blob;
use crate::live::{LiveError, LiveSet};
use crate::pipeline::SearchPipeline;
use crate::qbi::utf16_to_string;
use crate::search::{RankGroup, SearchItemKind, SearchResponse};

pub const STATUS_OK: i32 = 0;
pub const STATUS_CANCELED: i32 = 1;
pub const STATUS_BASE_CHANGED: i32 = 2;
pub const STATUS_INVALID_ARGUMENT: i32 = 3;
pub const STATUS_INTERNAL: i32 = 4;
pub const STATUS_INVALID_HANDLE: i32 = 5;

const RESULT_MAGIC: &[u8; 8] = b"QBXRES01";
const RESULT_VERSION: u32 = 1;
const NULL_LENGTH: u32 = u32::MAX;

#[repr(C)]
pub struct QboxBuffer {
    pub ptr: *mut u8,
    pub len: usize,
}

pub struct EngineHandle {
    pipeline: RwLock<Arc<SearchPipeline>>,
    canceled_through: AtomicI64,
    last_error: Mutex<String>,
}

impl EngineHandle {
    fn set_error(&self, message: impl Into<String>) {
        *self.last_error.lock().expect("last error") = message.into();
    }
}

fn status(code: i32) -> i32 {
    code
}

fn guard(handle: &EngineHandle, work: impl FnOnce() -> Result<i32, i32>) -> i32 {
    match catch_unwind(AssertUnwindSafe(work)) {
        Ok(Ok(code)) => status(code),
        Ok(Err(code)) => code,
        Err(_) => {
            handle.set_error("Rust 引擎发生内部异常");
            STATUS_INTERNAL
        }
    }
}

unsafe fn read_utf16(pointer: *const u16, length: usize) -> Result<String, i32> {
    if length == 0 {
        return Ok(String::new());
    }
    if pointer.is_null() {
        return Err(STATUS_INVALID_ARGUMENT);
    }
    Ok(utf16_to_string(slice::from_raw_parts(pointer, length)))
}

unsafe fn write_utf16(pointer: *mut u16, capacity: usize, value: &str) -> usize {
    if pointer.is_null() || capacity == 0 {
        return 0;
    }
    let units: Vec<u16> = value.encode_utf16().collect();
    let count = units.len().min(capacity - 1);
    ptr::copy_nonoverlapping(units.as_ptr(), pointer, count);
    *pointer.add(count) = 0;
    count
}

fn append_units(bytes: &mut Vec<u8>, value: &str) {
    for unit in value.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
}

fn optional_length(value: Option<&str>) -> u32 {
    match value {
        Some(value) => value.encode_utf16().count() as u32,
        None => NULL_LENGTH,
    }
}

fn kind_byte(kind: SearchItemKind) -> u8 {
    match kind {
        SearchItemKind::File => 0,
        SearchItemKind::Directory => 1,
        SearchItemKind::Application => 2,
        SearchItemKind::Shortcut => 3,
    }
}

fn group_byte(group: RankGroup) -> u8 {
    match group {
        RankGroup::ExactQueryHistory => 0,
        RankGroup::PrefixQueryHistory => 1,
        RankGroup::Ordinary => 2,
        RankGroup::ApplicationEntry => 3,
        RankGroup::ExecutableEntry => 4,
    }
}

fn serialize_response(response: &SearchResponse) -> Box<[u8]> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(RESULT_MAGIC);
    bytes.extend_from_slice(&RESULT_VERSION.to_le_bytes());
    bytes.extend_from_slice(&response.total_matches.to_le_bytes());
    bytes.extend_from_slice(&(response.items.len() as u32).to_le_bytes());
    bytes.extend_from_slice(&(response.query.encode_utf16().count() as u32).to_le_bytes());
    append_units(&mut bytes, &response.query);
    for hit in &response.items {
        let candidate = &hit.candidate;
        let arguments = candidate.arguments.as_deref();
        let aumid = candidate.app_user_model_id.as_deref();
        let mut flags = 0u8;
        if candidate.is_application_entry {
            flags |= 1;
        }
        if candidate.is_direct_executable {
            flags |= 2;
        }
        if hit.match_result.is_match {
            flags |= 4;
        }
        let mut history_flags = 0u8;
        if hit.history.exact_query.is_some() {
            history_flags |= 1;
        }
        if hit.history.prefix_query.is_some() {
            history_flags |= 2;
        }
        if hit.history.last_used.is_some() {
            history_flags |= 4;
        }
        bytes.push(kind_byte(candidate.kind));
        bytes.push(group_byte(hit.group));
        bytes.push(flags);
        bytes.push(history_flags);
        bytes.extend_from_slice(&hit.match_result.alignment_score.to_le_bytes());
        bytes.extend_from_slice(&candidate.normal_priority.to_le_bytes());
        bytes.extend_from_slice(&(hit.match_result.highlights.len() as u32).to_le_bytes());
        // 长度与内容交错，C# 侧逐项读取，避免两套字段顺序。
        bytes.extend_from_slice(&(candidate.id.encode_utf16().count() as u32).to_le_bytes());
        append_units(&mut bytes, &candidate.id);
        bytes.extend_from_slice(&(candidate.name.encode_utf16().count() as u32).to_le_bytes());
        append_units(&mut bytes, &candidate.name);
        bytes.extend_from_slice(&(candidate.target.encode_utf16().count() as u32).to_le_bytes());
        append_units(&mut bytes, &candidate.target);
        bytes.extend_from_slice(&optional_length(arguments).to_le_bytes());
        if let Some(arguments) = arguments {
            append_units(&mut bytes, arguments);
        }
        bytes.extend_from_slice(&optional_length(aumid).to_le_bytes());
        if let Some(aumid) = aumid {
            append_units(&mut bytes, aumid);
        }
        if let Some(value) = hit.history.exact_query {
            bytes.extend_from_slice(&value.0.to_le_bytes());
        }
        if let Some(value) = hit.history.prefix_query {
            bytes.extend_from_slice(&value.0.to_le_bytes());
        }
        if let Some(value) = hit.history.last_used {
            bytes.extend_from_slice(&value.0.to_le_bytes());
        }
        for range in &hit.match_result.highlights {
            bytes.extend_from_slice(&range.start.to_le_bytes());
            bytes.extend_from_slice(&range.length.to_le_bytes());
        }
    }
    bytes.into_boxed_slice()
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_open(
    index_dir: *const u16,
    index_dir_length: usize,
    flags: u32,
    out_handle: *mut *mut EngineHandle,
    error_buffer: *mut u16,
    error_capacity: usize,
) -> i32 {
    if out_handle.is_null() {
        return STATUS_INVALID_ARGUMENT;
    }
    *out_handle = ptr::null_mut();
    let result = catch_unwind(AssertUnwindSafe(|| {
        if flags != 0 {
            return Err("不支持的引擎标志".to_string());
        }
        let directory = read_utf16(index_dir, index_dir_length)
            .map_err(|_| "索引目录参数无效".to_string())?;
        let live = LiveSet::load(&directory, CancelToken::NONE)
            .map_err(|error| format!("索引加载失败：{error}"))?;
        let pipeline = SearchPipeline::with_empty_catalogs(Arc::new(live));
        Ok(Box::into_raw(Box::new(EngineHandle {
            pipeline: RwLock::new(Arc::new(pipeline)),
            canceled_through: AtomicI64::new(i64::MIN),
            last_error: Mutex::new(String::new()),
        })))
    }));
    match result {
        Ok(Ok(handle)) => {
            *out_handle = handle;
            STATUS_OK
        }
        Ok(Err(message)) => {
            write_utf16(error_buffer, error_capacity, &message);
            STATUS_INTERNAL
        }
        Err(_) => {
            write_utf16(error_buffer, error_capacity, "Rust 引擎加载时发生内部异常");
            STATUS_INTERNAL
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_prepare(handle: *mut EngineHandle) -> i32 {
    if handle.is_null() {
        return STATUS_INVALID_HANDLE;
    }
    let handle = &*handle;
    guard(handle, || {
        // open 已完成 PrepareSearch；保留独立入口供 C# 与方案 ABI 对齐。
        Ok(STATUS_OK)
    })
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_refresh(
    handle: *mut EngineHandle,
    catalog_json: *const u8,
    catalog_length: usize,
) -> i32 {
    if handle.is_null() {
        return STATUS_INVALID_HANDLE;
    }
    let handle = &*handle;
    guard(handle, || {
        let current = handle
            .pipeline
            .read()
            .expect("pipeline")
            .clone();
        let refreshed = match current.live.refresh(CancelToken::NONE) {
            Ok(value) => value,
            Err(LiveError::BaseChanged) => return Ok(STATUS_BASE_CHANGED),
            Err(error) => {
                handle.set_error(format!("实时索引刷新失败：{error}"));
                return Ok(STATUS_INTERNAL);
            }
        };
        let live = match refreshed {
            Some(set) => Arc::new(set),
            None => Arc::clone(&current.live),
        };
        let next = if catalog_length == 0 {
            SearchPipeline {
                applications: Arc::clone(&current.applications),
                shortcuts: Arc::clone(&current.shortcuts),
                live,
            }
        } else {
            if catalog_json.is_null() {
                handle.set_error("目录快照指针为空");
                return Ok(STATUS_INVALID_ARGUMENT);
            }
            let bytes = slice::from_raw_parts(catalog_json, catalog_length);
            match SearchPipeline::from_catalog_json(live, bytes) {
                Ok(pipeline) => pipeline,
                Err(error) => {
                    handle.set_error(error);
                    return Ok(STATUS_INVALID_ARGUMENT);
                }
            }
        };
        *handle.pipeline.write().expect("pipeline") = Arc::new(next);
        Ok(STATUS_OK)
    })
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_search(
    handle: *mut EngineHandle,
    request_id: i64,
    query: *const u16,
    query_length: usize,
    limit: i32,
    history: *const u8,
    history_length: usize,
    out_result: *mut QboxBuffer,
) -> i32 {
    if handle.is_null() {
        return STATUS_INVALID_HANDLE;
    }
    if out_result.is_null() {
        return STATUS_INVALID_ARGUMENT;
    }
    *out_result = QboxBuffer {
        ptr: ptr::null_mut(),
        len: 0,
    };
    if limit < 0 {
        return STATUS_INVALID_ARGUMENT;
    }
    let handle = &*handle;
    guard(handle, || {
        let query = match read_utf16(query, query_length) {
            Ok(query) => query,
            Err(code) => return Ok(code),
        };
        let history = if history_length == 0 {
            Ok(crate::history::QueryHistory::new())
        } else if history.is_null() {
            Err("历史快照指针为空".to_string())
        } else {
            parse_history_blob(slice::from_raw_parts(history, history_length))
                .map_err(|error| format!("历史快照无效：{error}"))
        };
        let history = match history {
            Ok(history) => history,
            Err(error) => {
                handle.set_error(error);
                return Ok(STATUS_INVALID_ARGUMENT);
            }
        };
        let pipeline = handle.pipeline.read().expect("pipeline").clone();
        let token = CancelToken::from_flag(&handle.canceled_through, request_id);
        match pipeline.search(&query, limit, &history, token) {
            Ok(response) => {
                let boxed = serialize_response(&response);
                let length = boxed.len();
                let pointer = Box::into_raw(boxed) as *mut u8;
                *out_result = QboxBuffer {
                    ptr: pointer,
                    len: length,
                };
                Ok(STATUS_OK)
            }
            Err(_) => Ok(STATUS_CANCELED),
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_cancel(handle: *mut EngineHandle, request_id: i64) -> i32 {
    if handle.is_null() {
        return STATUS_INVALID_HANDLE;
    }
    (*handle)
        .canceled_through
        .fetch_max(request_id, Ordering::Relaxed);
    STATUS_OK
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_free_buffer(buffer: QboxBuffer) {
    if buffer.ptr.is_null() {
        return;
    }
    let slice = ptr::slice_from_raw_parts_mut(buffer.ptr, buffer.len);
    drop(Box::from_raw(slice));
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_last_error(
    handle: *mut EngineHandle,
    buffer: *mut u16,
    capacity: usize,
) -> i32 {
    if handle.is_null() {
        return STATUS_INVALID_HANDLE;
    }
    let message = (*handle)
        .last_error
        .lock()
        .expect("last error")
        .clone();
    write_utf16(buffer, capacity, &message) as i32
}

#[no_mangle]
pub unsafe extern "C" fn qbox_engine_close(handle: *mut EngineHandle) {
    if !handle.is_null() {
        drop(Box::from_raw(handle));
    }
}

//! 对应 C# `CancellationToken` 的最小子集，供拼音与收集器在已取消时抛出。

use std::fmt;
use std::sync::atomic::{AtomicI64, Ordering};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CancelToken {
    kind: CancelKind,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CancelKind {
    Never,
    Always,
    // FFI 调用期间 handle 与其中的 AtomicI64 必须保持存活。
    Flag {
        flag: *const AtomicI64,
        request_id: i64,
    },
}

impl CancelToken {
    pub const NONE: Self = Self {
        kind: CancelKind::Never,
    };
    pub const CANCELED: Self = Self {
        kind: CancelKind::Always,
    };

    /// # Safety
    /// `flag` 必须在 token 使用期间保持有效；FFI 句柄的生命周期由 C# SafeHandle 保证。
    pub unsafe fn from_flag(flag: &AtomicI64, request_id: i64) -> Self {
        Self {
            kind: CancelKind::Flag {
                flag,
                request_id,
            },
        }
    }

    pub fn check(self) -> Result<(), Canceled> {
        match self.kind {
            CancelKind::Never => Ok(()),
            CancelKind::Always => Err(Canceled),
            CancelKind::Flag { flag, request_id } => {
                // 取消阈值只增不减；更早的查询被取消，之后的新查询不受影响。
                if unsafe { (*flag).load(Ordering::Relaxed) } >= request_id {
                    Err(Canceled)
                } else {
                    Ok(())
                }
            }
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Canceled;

impl fmt::Display for Canceled {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "OperationCanceledException")
    }
}

impl std::error::Error for Canceled {}

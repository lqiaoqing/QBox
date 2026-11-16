use std::fs;
use std::path::PathBuf;
use std::process::ExitCode;

use qbox_engine::checks::{run_launcher_ordering_checks, run_pinyin_checks};
use qbox_engine::oracle::{default_fixture_dir, run_ascii_oracle};

fn main() -> ExitCode {
    let mut failed = 0;
    match run_ascii_oracle(&default_fixture_dir(), true) {
        Ok(report) => {
            println!(
                "匹配检查：{}/{} 通过；留出未执行：{}",
                report.passed,
                report.executed,
                report.total.saturating_sub(report.executed)
            );
            failed += report.failed();
        }
        Err(error) => {
            eprintln!("匹配检查失败：{error}");
            failed += 1;
        }
    }

    let pinyin = run_pinyin_checks();
    println!("拼音检查：{}/{} 通过", pinyin.passed(), pinyin.total);
    failed += pinyin.failures.len();

    match run_launcher_ordering_checks() {
        Ok(ordering) => {
            println!(
                "排序收集检查：{}/{} 通过",
                ordering.passed(),
                ordering.total
            );
            failed += ordering.failures.len();
            write_failures(&pinyin.failures, &ordering.failures);
        }
        Err(error) => {
            eprintln!("排序收集检查失败：{error}");
            failed += 1;
            write_failures(&pinyin.failures, &[]);
        }
    }

    if failed == 0 {
        ExitCode::SUCCESS
    } else {
        ExitCode::from(1)
    }
}

fn write_failures(
    pinyin: &[qbox_engine::checks::CheckFailure],
    ordering: &[qbox_engine::checks::CheckFailure],
) {
    let notes_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("work")
        .join("rust-validation");
    let _ = fs::create_dir_all(&notes_dir);
    let mut output = String::new();
    output.push_str(&format!(
        "pinyin_failed={} ordering_failed={}\n",
        pinyin.len(),
        ordering.len()
    ));
    for (index, failure) in pinyin.iter().chain(ordering.iter()).enumerate() {
        let line = format!("FAIL {}: {}\n", failure.name, failure.message);
        if index < 25 {
            eprint!("{line}");
        }
        output.push_str(&line);
    }
    let path = notes_dir.join("engine-failures.txt");
    if let Err(error) = fs::write(&path, output) {
        eprintln!("无法写入 {}: {error}", path.display());
    } else {
        println!("失败明细：{}", path.display());
    }
}

use std::env;
use std::fs;
use std::path::PathBuf;
use std::process::ExitCode;

use qbox_engine::oracle::{default_fixture_dir, run_ascii_oracle};

fn main() -> ExitCode {
    let args: Vec<String> = env::args().skip(1).collect();
    let include_holdout = !args.iter().any(|arg| arg == "--skip-holdout");
    let fixture_dir = args
        .iter()
        .find(|arg| !arg.starts_with("--"))
        .map(PathBuf::from)
        .unwrap_or_else(default_fixture_dir);

    match run_ascii_oracle(&fixture_dir, include_holdout) {
        Ok(report) => {
            println!(
                "匹配检查：{}/{} 通过；留出未执行：{}",
                report.passed,
                report.executed,
                report.total.saturating_sub(report.executed)
            );
            let notes_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("..")
                .join("..")
                .join("work")
                .join("rust-validation");
            let _ = fs::create_dir_all(&notes_dir);
            let failure_path = notes_dir.join("failures.txt");
            let mut output = String::new();
            output.push_str(&format!(
                "executed={} passed={} failed={} total_fixtures={}\n",
                report.executed,
                report.passed,
                report.failed(),
                report.total
            ));
            for (index, failure) in report.failures.iter().enumerate() {
                let line = format!(
                    "FAIL query={:?} candidate={:?} partition={:?} expected={} match={} is_match={} score_only={}: {}\n",
                    failure.query,
                    failure.candidate,
                    failure.partition,
                    failure.expected,
                    failure.match_score,
                    failure.match_is_match,
                    failure.score_only,
                    failure.reason
                );
                if index < 25 {
                    eprint!("{line}");
                }
                output.push_str(&line);
            }
            if let Err(error) = fs::write(&failure_path, output) {
                eprintln!("无法写入 {}: {error}", failure_path.display());
            } else {
                println!("失败明细：{}", failure_path.display());
            }
            if report.failed() == 0 {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            }
        }
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(2)
        }
    }
}

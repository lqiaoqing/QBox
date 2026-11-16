//! 对应 C# `CancellationToken` 的最小子集，供拼音与收集器在已取消时抛出。

use std::fmt;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CancelToken {
    canceled: bool,
}

impl CancelToken {
    pub const NONE: Self = Self { canceled: false };
    pub const CANCELED: Self = Self { canceled: true };

    pub fn check(self) -> Result<(), Canceled> {
        if self.canceled {
            Err(Canceled)
        } else {
            Ok(())
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

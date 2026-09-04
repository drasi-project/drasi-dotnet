// Copyright 2026 The Drasi Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

//! Thread-local last-error storage for the C ABI.
//!
//! Native functions never unwind into the CLR. Failures are reported as a
//! non-zero status, a stable code from [`drasi_last_error_code`], and a UTF-8
//! message from [`drasi_last_error`].

#![allow(clippy::missing_safety_doc)]

use std::cell::RefCell;
use std::ffi::{CStr, CString};
use std::fmt;
use std::os::raw::c_char;
use std::ptr;

thread_local! {
    static LAST_ERROR: RefCell<Option<StoredError>> = const { RefCell::new(None) };
}

pub const OK: i32 = 0;
pub const ERR: i32 = -1;

/// Stable, machine-readable failure codes. Names match the Python/Node hosts
/// except language-specific source codes (`NO_CSHARP_SOURCE` vs `NO_PY_SOURCE`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(dead_code)] // codes kept for parity with Python/Node even before plugins land
pub enum ErrorCode {
    UnknownSourceKind,
    UnknownReactionKind,
    UnknownBootstrapKind,
    UnknownSecretStoreKind,
    BootstrapKindRequired,
    NoCsharpSource,
    ChangeNotObject,
    ChangeOpRequired,
    ChangeIdRequired,
    RelationRequiresBothEnds,
    UnknownChangeOp,
    StateStorePathRequired,
    UnknownStateStoreKind,
    IndexStorePathRequired,
    UnknownIndexStoreKind,
    IdentityKindRequired,
    UnknownIdentityKind,
    IdentityConfigInvalid,
    DurableRequiresStateStore,
    UnknownQueryLanguage,
    ConfigInvalid,
    PluginSignatureInvalid,
    PluginIncompatible,
    PluginNotFound,
    StreamLagged,
    EngineClosed,
    EngineFailure,
}

impl ErrorCode {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::UnknownSourceKind => "UNKNOWN_SOURCE_KIND",
            Self::UnknownReactionKind => "UNKNOWN_REACTION_KIND",
            Self::UnknownBootstrapKind => "UNKNOWN_BOOTSTRAP_KIND",
            Self::UnknownSecretStoreKind => "UNKNOWN_SECRET_STORE_KIND",
            Self::BootstrapKindRequired => "BOOTSTRAP_KIND_REQUIRED",
            Self::NoCsharpSource => "NO_CSHARP_SOURCE",
            Self::ChangeNotObject => "CHANGE_NOT_OBJECT",
            Self::ChangeOpRequired => "CHANGE_OP_REQUIRED",
            Self::ChangeIdRequired => "CHANGE_ID_REQUIRED",
            Self::RelationRequiresBothEnds => "RELATION_REQUIRES_BOTH_ENDS",
            Self::UnknownChangeOp => "UNKNOWN_CHANGE_OP",
            Self::StateStorePathRequired => "STATE_STORE_PATH_REQUIRED",
            Self::UnknownStateStoreKind => "UNKNOWN_STATE_STORE_KIND",
            Self::IndexStorePathRequired => "INDEX_STORE_PATH_REQUIRED",
            Self::UnknownIndexStoreKind => "UNKNOWN_INDEX_STORE_KIND",
            Self::IdentityKindRequired => "IDENTITY_KIND_REQUIRED",
            Self::UnknownIdentityKind => "UNKNOWN_IDENTITY_KIND",
            Self::IdentityConfigInvalid => "IDENTITY_CONFIG_INVALID",
            Self::DurableRequiresStateStore => "DURABLE_REQUIRES_STATE_STORE",
            Self::UnknownQueryLanguage => "UNKNOWN_QUERY_LANGUAGE",
            Self::ConfigInvalid => "CONFIG_INVALID",
            Self::PluginSignatureInvalid => "PLUGIN_SIGNATURE_INVALID",
            Self::PluginIncompatible => "PLUGIN_INCOMPATIBLE",
            Self::PluginNotFound => "PLUGIN_NOT_FOUND",
            Self::StreamLagged => "STREAM_LAGGED",
            Self::EngineClosed => "ENGINE_CLOSED",
            Self::EngineFailure => "ENGINE_FAILURE",
        }
    }

    #[allow(dead_code)]
    pub fn all() -> &'static [ErrorCode] {
        use ErrorCode::*;
        &[
            UnknownSourceKind,
            UnknownReactionKind,
            UnknownBootstrapKind,
            UnknownSecretStoreKind,
            BootstrapKindRequired,
            NoCsharpSource,
            ChangeNotObject,
            ChangeOpRequired,
            ChangeIdRequired,
            RelationRequiresBothEnds,
            UnknownChangeOp,
            StateStorePathRequired,
            UnknownStateStoreKind,
            IndexStorePathRequired,
            UnknownIndexStoreKind,
            IdentityKindRequired,
            UnknownIdentityKind,
            IdentityConfigInvalid,
            DurableRequiresStateStore,
            UnknownQueryLanguage,
            ConfigInvalid,
            PluginSignatureInvalid,
            PluginIncompatible,
            PluginNotFound,
            StreamLagged,
            EngineClosed,
            EngineFailure,
        ]
    }
}

struct StoredError {
    code: CString,
    message: CString,
}

/// Failure returned by native helpers and mapped onto the last-error slots.
#[derive(Debug)]
pub struct FfiError {
    pub code: ErrorCode,
    pub message: String,
}

impl FfiError {
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    pub fn engine(err: impl fmt::Display) -> Self {
        Self::new(ErrorCode::EngineFailure, err.to_string())
    }

    pub fn closed(id: &str) -> Self {
        Self::new(
            ErrorCode::EngineClosed,
            format!("engine '{id}' has been closed"),
        )
    }

    pub fn config(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::ConfigInvalid, message)
    }

    pub fn plugin(err: impl fmt::Display) -> Self {
        Self::new(ErrorCode::PluginNotFound, err.to_string())
    }

    pub fn incompatible(err: impl fmt::Display) -> Self {
        Self::new(
            ErrorCode::PluginIncompatible,
            format!("{err}\nthis host is {}", crate::plugins::describe_host()),
        )
    }

    pub fn unknown_kind(code: ErrorCode, what: &str, kind: &str) -> Self {
        Self::new(code, format!("unknown {what} kind '{kind}'"))
    }
}

impl fmt::Display for FfiError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl From<String> for FfiError {
    fn from(message: String) -> Self {
        Self::engine(message)
    }
}

pub type FfiResult<T> = Result<T, FfiError>;

pub fn set_error(err: &FfiError) {
    let code = CString::new(err.code.as_str()).unwrap_or_else(|_| c"ENGINE_FAILURE".into());
    let message = CString::new(err.message.replace('\0', ""))
        .unwrap_or_else(|_| CString::new("error").expect("static"));
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = Some(StoredError { code, message });
    });
}

pub fn set_last_error(message: impl fmt::Display) {
    set_error(&FfiError::engine(message));
}

pub fn clear_last_error() {
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = None;
    });
}

/// Pointer is valid until the next error is recorded on this thread. The
/// caller must copy the bytes; do not free this pointer.
#[no_mangle]
pub extern "C" fn drasi_last_error() -> *const c_char {
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some(value) => value.message.as_ptr(),
        None => ptr::null(),
    })
}

/// Stable code for the last error on this thread, or null.
#[no_mangle]
pub extern "C" fn drasi_last_error_code() -> *const c_char {
    LAST_ERROR.with(|slot| match slot.borrow().as_ref() {
        Some(value) => value.code.as_ptr(),
        None => ptr::null(),
    })
}

pub unsafe fn read_utf8<'a>(ptr: *const c_char, name: &str) -> FfiResult<&'a str> {
    if ptr.is_null() {
        return Err(FfiError::config(format!("{name} is null")));
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map_err(|err| FfiError::config(format!("{name} is not valid UTF-8: {err}")))
}

pub unsafe fn read_utf8_opt<'a>(ptr: *const c_char) -> FfiResult<Option<&'a str>> {
    if ptr.is_null() {
        return Ok(None);
    }
    let value = read_utf8(ptr, "value")?;
    if value.is_empty() {
        Ok(None)
    } else {
        Ok(Some(value))
    }
}

pub fn alloc_utf8(value: &str) -> *mut c_char {
    match CString::new(value) {
        Ok(cstr) => cstr.into_raw(),
        Err(_) => {
            set_error(&FfiError::engine("result contained an interior NUL"));
            ptr::null_mut()
        }
    }
}

/// Frees a string allocated by this library (for example query results).
#[no_mangle]
pub unsafe extern "C" fn drasi_string_free(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    #[test]
    fn every_code_has_a_unique_screaming_snake_name() {
        let mut seen = HashSet::new();
        for code in ErrorCode::all() {
            let name = code.as_str();
            assert!(seen.insert(name), "duplicate error code: {name}");
            assert!(
                !name.is_empty()
                    && name
                        .chars()
                        .all(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || c == '_'),
                "{name} is not SCREAMING_SNAKE_CASE"
            );
        }
    }
}

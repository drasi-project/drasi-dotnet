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
//! non-zero status and a UTF-8 message retrieved with [`drasi_last_error`].

#![allow(clippy::missing_safety_doc)]

use std::cell::RefCell;
use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::ptr;

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

pub const OK: i32 = 0;
pub const ERR: i32 = -1;

pub fn set_last_error(message: impl std::fmt::Display) {
    let text = message.to_string().replace('\0', "");
    let cstr = CString::new(text).unwrap_or_else(|_| CString::new("error").expect("static"));
    LAST_ERROR.with(|slot| {
        *slot.borrow_mut() = Some(cstr);
    });
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
        Some(value) => value.as_ptr(),
        None => ptr::null(),
    })
}

pub unsafe fn read_utf8<'a>(ptr: *const c_char, name: &str) -> Result<&'a str, String> {
    if ptr.is_null() {
        return Err(format!("{name} is null"));
    }
    CStr::from_ptr(ptr)
        .to_str()
        .map_err(|err| format!("{name} is not valid UTF-8: {err}"))
}

pub fn alloc_utf8(value: &str) -> *mut c_char {
    match CString::new(value) {
        Ok(cstr) => cstr.into_raw(),
        Err(_) => {
            set_last_error("result contained an interior NUL");
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

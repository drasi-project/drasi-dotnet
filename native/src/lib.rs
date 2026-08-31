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

//! C ABI for embedding Drasi in .NET.
//!
//! This crate must never install a custom global allocator. A later plugin
//! host will exchange heap allocations across the FFI boundary, which is only
//! sound while both sides use the process-global system allocator.
//!
//! Every exported function catches panics so they cannot unwind into the CLR.

#![allow(clippy::missing_safety_doc)]
#![allow(clippy::not_unsafe_ptr_arg_deref)]

mod components;
mod conversions;
mod engine;
mod error;

use std::os::raw::{c_char, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::OnceLock;

use tokio::runtime::{Builder, Runtime};

use crate::components::ReactionCallback;
use crate::engine::EngineHandle;
use crate::error::{alloc_utf8, clear_last_error, read_utf8, set_last_error, ERR, OK};

fn runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        Builder::new_multi_thread()
            .enable_all()
            .thread_name("drasi-worker")
            .build()
            .expect("failed to build the Drasi tokio runtime")
    })
}

fn init_logging() {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        let filter = tracing_subscriber::EnvFilter::try_from_default_env()
            .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("warn"));
        let _ = tracing_subscriber::fmt().with_env_filter(filter).try_init();
    });
}

fn catch_status(f: impl FnOnce() -> Result<(), String>) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => {
            clear_last_error();
            OK
        }
        Ok(Err(err)) => {
            set_last_error(err);
            ERR
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ERR
        }
    }
}

unsafe fn handle_ref<'a>(engine: *mut EngineHandle) -> Result<&'a EngineHandle, String> {
    engine
        .as_ref()
        .ok_or_else(|| "engine handle is null".to_string())
}

/// Creates an in-process engine. Returns null on failure; see [`drasi_last_error`].
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_create(id: *const c_char) -> *mut EngineHandle {
    init_logging();
    match catch_unwind(AssertUnwindSafe(|| {
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::create(id))
    })) {
        Ok(Ok(handle)) => {
            clear_last_error();
            Box::into_raw(Box::new(handle))
        }
        Ok(Err(err)) => {
            set_last_error(err);
            ptr::null_mut()
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ptr::null_mut()
        }
    }
}

/// Stops the engine and frees the handle. Null is a no-op.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_destroy(engine: *mut EngineHandle) {
    if engine.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let handle = Box::from_raw(engine);
        let _ = runtime().block_on(engine::shutdown(&handle));
    }));
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_start(engine: *mut EngineHandle) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::start(handle))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_shutdown(engine: *mut EngineHandle) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::shutdown(handle))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_source(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::add_source(handle, id))
    })
}

/// `sources_json` is a JSON array of source ids, e.g. `["orders"]`.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_query(
    engine: *mut EngineHandle,
    id: *const c_char,
    query: *const c_char,
    sources_json: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let query = read_utf8(query, "query")?.to_string();
        let sources_json = read_utf8(sources_json, "sources_json")?;
        runtime().block_on(engine::add_query(handle, id, query, sources_json))
    })
}

/// `query_ids_json` is a JSON array of query ids. `callback` is invoked from a
/// tokio worker with a transient UTF-8 JSON pointer; copy before returning.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_reaction(
    engine: *mut EngineHandle,
    id: *const c_char,
    query_ids_json: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let query_ids_json = read_utf8(query_ids_json, "query_ids_json")?;
        let callback = callback.ok_or_else(|| "reaction callback is null".to_string())?;
        runtime().block_on(engine::add_reaction(
            handle,
            id,
            query_ids_json,
            callback,
            user_data,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_push_change(
    engine: *mut EngineHandle,
    source_id: *const c_char,
    change_json: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let source_id = read_utf8(source_id, "source_id")?.to_string();
        let change_json = read_utf8(change_json, "change_json")?;
        runtime().block_on(engine::push_change(handle, source_id, change_json))
    })
}

/// Returns a heap-allocated JSON array of current rows. Caller must
/// [`drasi_string_free`] it. Null on failure.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_get_query_results(
    engine: *mut EngineHandle,
    query_id: *const c_char,
) -> *mut c_char {
    match catch_unwind(AssertUnwindSafe(|| {
        let handle = handle_ref(engine)?;
        let query_id = read_utf8(query_id, "query_id")?.to_string();
        runtime().block_on(engine::get_query_results(handle, query_id))
    })) {
        Ok(Ok(json)) => {
            clear_last_error();
            alloc_utf8(&json)
        }
        Ok(Err(err)) => {
            set_last_error(err);
            ptr::null_mut()
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ptr::null_mut()
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_wait_for_query(
    engine: *mut EngineHandle,
    query_id: *const c_char,
    timeout_seconds: f64,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let query_id = read_utf8(query_id, "query_id")?.to_string();
        runtime().block_on(engine::wait_for_query(handle, query_id, timeout_seconds))
    })
}

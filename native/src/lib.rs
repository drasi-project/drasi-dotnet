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
mod host;
mod logging;
mod plugins;
mod secrets;
mod stores;
mod streams;

use std::os::raw::{c_char, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::OnceLock;

use tokio::runtime::{Builder, Runtime};

use crate::components::ReactionCallback;
use crate::engine::EngineHandle;
use crate::error::{
    alloc_utf8, clear_last_error, read_utf8, read_utf8_opt, set_error, set_last_error, FfiError,
    FfiResult, ERR, OK,
};
use crate::streams::StreamHandle;

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
    crate::logging::init();
}

fn catch_status(f: impl FnOnce() -> FfiResult<()>) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => {
            clear_last_error();
            OK
        }
        Ok(Err(err)) => {
            set_error(&err);
            ERR
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ERR
        }
    }
}

fn catch_string(f: impl FnOnce() -> FfiResult<String>) -> *mut c_char {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(json)) => {
            clear_last_error();
            alloc_utf8(&json)
        }
        Ok(Err(err)) => {
            set_error(&err);
            ptr::null_mut()
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ptr::null_mut()
        }
    }
}

unsafe fn handle_ref<'a>(engine: *mut EngineHandle) -> FfiResult<&'a EngineHandle> {
    engine
        .as_ref()
        .ok_or_else(|| FfiError::config("engine handle is null"))
}

unsafe fn required_callback(callback: Option<ReactionCallback>) -> FfiResult<ReactionCallback> {
    callback.ok_or_else(|| FfiError::config("callback is null"))
}

/// Creates an in-process engine. Returns null on failure; see [`drasi_last_error`].
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_create(id: *const c_char) -> *mut EngineHandle {
    init_logging();
    match catch_unwind(AssertUnwindSafe(|| {
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::create(id, None))
    })) {
        Ok(Ok(handle)) => {
            clear_last_error();
            Box::into_raw(Box::new(handle))
        }
        Ok(Err(err)) => {
            set_error(&err);
            ptr::null_mut()
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ptr::null_mut()
        }
    }
}

/// Creates an engine with JSON options (secrets, stores, identity, pluginsDir).
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_create_with_options(
    id: *const c_char,
    options_json: *const c_char,
) -> *mut EngineHandle {
    init_logging();
    match catch_unwind(AssertUnwindSafe(|| {
        let id = read_utf8(id, "id")?.to_string();
        let options = read_utf8_opt(options_json)?;
        runtime().block_on(engine::create(id, options))
    })) {
        Ok(Ok(handle)) => {
            clear_last_error();
            Box::into_raw(Box::new(handle))
        }
        Ok(Err(err)) => {
            set_error(&err);
            ptr::null_mut()
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ptr::null_mut()
        }
    }
}

/// Builds an engine from a JSON document of C# sources and queries.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_from_config(config_json: *const c_char) -> *mut EngineHandle {
    init_logging();
    match catch_unwind(AssertUnwindSafe(|| {
        let json = read_utf8(config_json, "config_json")?;
        runtime().block_on(engine::from_config(json))
    })) {
        Ok(Ok(handle)) => {
            clear_last_error();
            Box::into_raw(Box::new(handle))
        }
        Ok(Err(err)) => {
            set_error(&err);
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
pub unsafe extern "C" fn drasi_engine_stop(engine: *mut EngineHandle) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::stop(handle))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_shutdown(engine: *mut EngineHandle) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::shutdown(handle))
    })
}

/// 1 if running, 0 if not, -1 on error.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_is_running(engine: *mut EngineHandle) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        let handle = handle_ref(engine)?;
        Ok::<_, FfiError>(runtime().block_on(engine::is_running(handle)))
    })) {
        Ok(Ok(true)) => {
            clear_last_error();
            1
        }
        Ok(Ok(false)) => {
            clear_last_error();
            0
        }
        Ok(Err(err)) => {
            set_error(&err);
            ERR
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ERR
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_source(
    engine: *mut EngineHandle,
    id: *const c_char,
    auto_start: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::add_source(handle, id, auto_start != 0))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_remove_source(
    engine: *mut EngineHandle,
    id: *const c_char,
    cleanup: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::remove_source(handle, id, cleanup != 0))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_start_source(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::start_source(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_stop_source(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::stop_source(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_source_status(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::source_status(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_list_sources(engine: *mut EngineHandle) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::list_sources(handle))
    })
}

/// `sources_json` is a JSON array of source ids or `{id, pipeline}` objects.
/// `options_json` is optional camelCase query options.
#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_query(
    engine: *mut EngineHandle,
    id: *const c_char,
    query: *const c_char,
    sources_json: *const c_char,
    options_json: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let query = read_utf8(query, "query")?.to_string();
        let sources_json = read_utf8(sources_json, "sources_json")?;
        let options_json = read_utf8_opt(options_json)?;
        runtime().block_on(engine::add_query(
            handle,
            id,
            query,
            sources_json,
            options_json,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_update_query(
    engine: *mut EngineHandle,
    id: *const c_char,
    query: *const c_char,
    sources_json: *const c_char,
    options_json: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let query = read_utf8(query, "query")?.to_string();
        let sources_json = read_utf8(sources_json, "sources_json")?;
        let options_json = read_utf8_opt(options_json)?;
        runtime().block_on(engine::update_query(
            handle,
            id,
            query,
            sources_json,
            options_json,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_remove_query(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::remove_query(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_start_query(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::start_query(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_stop_query(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::stop_query(handle, id))
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
    auto_start: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let query_ids_json = read_utf8(query_ids_json, "query_ids_json")?;
        let callback = required_callback(callback)?;
        runtime().block_on(engine::add_reaction(
            handle,
            id,
            query_ids_json,
            callback,
            user_data,
            auto_start != 0,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_remove_reaction(
    engine: *mut EngineHandle,
    id: *const c_char,
    cleanup: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::remove_reaction(handle, id, cleanup != 0))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_start_reaction(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::start_reaction(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_stop_reaction(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::stop_reaction(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_reaction_status(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::reaction_status(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_list_reactions(engine: *mut EngineHandle) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::list_reactions(handle))
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
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let query_id = read_utf8(query_id, "query_id")?.to_string();
        runtime().block_on(engine::get_query_results(handle, query_id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_query_status(
    engine: *mut EngineHandle,
    query_id: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let query_id = read_utf8(query_id, "query_id")?.to_string();
        runtime().block_on(engine::query_status(handle, query_id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_list_queries(engine: *mut EngineHandle) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::list_queries(handle))
    })
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

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_query_metrics(
    engine: *mut EngineHandle,
    query_id: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let query_id = read_utf8(query_id, "query_id")?.to_string();
        runtime().block_on(engine::query_metrics(handle, query_id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_reaction_metrics(
    engine: *mut EngineHandle,
    reaction_id: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let reaction_id = read_utf8(reaction_id, "reaction_id")?.to_string();
        runtime().block_on(engine::reaction_metrics(handle, reaction_id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_lifecycle_metrics(engine: *mut EngineHandle) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::lifecycle_metrics(handle))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_source_schema(
    engine: *mut EngineHandle,
    id: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        runtime().block_on(engine::source_schema(handle, id))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_graph_schema(engine: *mut EngineHandle) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::graph_schema(handle))
    })
}

unsafe fn catch_stream(f: impl FnOnce() -> FfiResult<StreamHandle>) -> *mut StreamHandle {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(handle)) => {
            clear_last_error();
            Box::into_raw(Box::new(handle))
        }
        Ok(Err(err)) => {
            set_error(&err);
            ptr::null_mut()
        }
        Err(_) => {
            set_last_error("panic in native Drasi code");
            ptr::null_mut()
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_query_events(
    engine: *mut EngineHandle,
    id: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_query_events(
            handle, id, callback, user_data,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_source_events(
    engine: *mut EngineHandle,
    id: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_source_events(
            handle, id, callback, user_data,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_reaction_events(
    engine: *mut EngineHandle,
    id: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_reaction_events(
            handle, id, callback, user_data,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_all_events(
    engine: *mut EngineHandle,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_all_events(handle, callback, user_data))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_query_logs(
    engine: *mut EngineHandle,
    id: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_query_logs(
            handle, id, callback, user_data,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_source_logs(
    engine: *mut EngineHandle,
    id: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_source_logs(
            handle, id, callback, user_data,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_subscribe_reaction_logs(
    engine: *mut EngineHandle,
    id: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
) -> *mut StreamHandle {
    catch_stream(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let callback = required_callback(callback)?;
        runtime().block_on(engine::subscribe_reaction_logs(
            handle, id, callback, user_data,
        ))
    })
}

/// Stops a stream started by `drasi_engine_subscribe_*`. Null is a no-op.
#[no_mangle]
pub unsafe extern "C" fn drasi_stream_close(stream: *mut StreamHandle) {
    if stream.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let handle = Box::from_raw(stream);
        handle.close();
    }));
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_durable_reaction(
    engine: *mut EngineHandle,
    id: *const c_char,
    query_ids_json: *const c_char,
    callback: Option<ReactionCallback>,
    user_data: *mut c_void,
    recovery_policy: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let id = read_utf8(id, "id")?.to_string();
        let query_ids_json = read_utf8(query_ids_json, "query_ids_json")?;
        let callback = required_callback(callback)?;
        let recovery = read_utf8(recovery_policy, "recovery_policy")?;
        runtime().block_on(engine::add_durable_reaction(
            handle,
            id,
            query_ids_json,
            callback,
            user_data,
            recovery,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_plugin_source(
    engine: *mut EngineHandle,
    kind: *const c_char,
    id: *const c_char,
    config_json: *const c_char,
    auto_start: i32,
    bootstrap_json: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        let id = read_utf8(id, "id")?.to_string();
        let config_json = read_utf8(config_json, "config_json")?;
        let bootstrap = read_utf8_opt(bootstrap_json)?;
        runtime().block_on(engine::add_plugin_source(
            handle,
            kind,
            id,
            config_json,
            auto_start != 0,
            bootstrap,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_add_plugin_reaction(
    engine: *mut EngineHandle,
    kind: *const c_char,
    id: *const c_char,
    query_ids_json: *const c_char,
    config_json: *const c_char,
    auto_start: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        let id = read_utf8(id, "id")?.to_string();
        let query_ids_json = read_utf8(query_ids_json, "query_ids_json")?;
        let config_json = read_utf8(config_json, "config_json")?;
        runtime().block_on(engine::add_plugin_reaction(
            handle,
            kind,
            id,
            query_ids_json,
            config_json,
            auto_start != 0,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_update_plugin_source(
    engine: *mut EngineHandle,
    kind: *const c_char,
    id: *const c_char,
    config_json: *const c_char,
    auto_start: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        let id = read_utf8(id, "id")?.to_string();
        let config_json = read_utf8(config_json, "config_json")?;
        runtime().block_on(engine::update_plugin_source(
            handle,
            kind,
            id,
            config_json,
            auto_start != 0,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_update_plugin_reaction(
    engine: *mut EngineHandle,
    kind: *const c_char,
    id: *const c_char,
    query_ids_json: *const c_char,
    config_json: *const c_char,
    auto_start: i32,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        let id = read_utf8(id, "id")?.to_string();
        let query_ids_json = read_utf8(query_ids_json, "query_ids_json")?;
        let config_json = read_utf8(config_json, "config_json")?;
        runtime().block_on(engine::update_plugin_reaction(
            handle,
            kind,
            id,
            query_ids_json,
            config_json,
            auto_start != 0,
        ))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_load_plugins(
    engine: *mut EngineHandle,
    directory: *const c_char,
    verify_json: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let directory = read_utf8(directory, "directory")?.to_string();
        let verify = read_utf8_opt(verify_json)?;
        runtime().block_on(engine::load_plugins(handle, directory, verify))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_watch_plugins(
    engine: *mut EngineHandle,
    directory: *const c_char,
    debounce_seconds: f64,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let directory = read_utf8(directory, "directory")?.to_string();
        runtime().block_on(engine::watch_plugins(handle, directory, debounce_seconds))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_plugin_kinds(engine: *mut EngineHandle) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        runtime().block_on(engine::plugin_kinds(handle))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_host_info() -> *mut c_char {
    catch_string(|| Ok(engine::host_info()))
}

#[no_mangle]
pub unsafe extern "C" fn drasi_search_plugins(query: *const c_char) -> *mut c_char {
    catch_string(|| {
        let query = read_utf8_opt(query)?;
        runtime().block_on(engine::search_plugins(query))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_list_plugin_tags(repository: *const c_char) -> *mut c_char {
    catch_string(|| {
        let repository = read_utf8(repository, "repository")?.to_string();
        runtime().block_on(engine::list_plugin_tags(repository))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_resolve_plugin(reference: *const c_char) -> *mut c_char {
    catch_string(|| {
        let reference = read_utf8(reference, "reference")?.to_string();
        runtime().block_on(engine::resolve_plugin(reference))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_install_plugin(
    engine: *mut EngineHandle,
    reference: *const c_char,
    options_json: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let reference = read_utf8(reference, "reference")?.to_string();
        let options = read_utf8_opt(options_json)?;
        runtime().block_on(engine::install_plugin(handle, reference, options))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_pull_plugin(
    reference: *const c_char,
    directory: *const c_char,
    filename: *const c_char,
    options_json: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let reference = read_utf8(reference, "reference")?.to_string();
        let directory = read_utf8(directory, "directory")?.to_string();
        let filename = read_utf8(filename, "filename")?.to_string();
        let options = read_utf8_opt(options_json)?;
        runtime().block_on(engine::pull_plugin(reference, directory, filename, options))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_write_lockfile(
    engine: *mut EngineHandle,
    directory: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let directory = read_utf8(directory, "directory")?.to_string();
        runtime().block_on(engine::write_lockfile(handle, directory))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_read_lockfile(directory: *const c_char) -> *mut c_char {
    catch_string(|| {
        let directory = read_utf8(directory, "directory")?.to_string();
        engine::read_lockfile(directory)
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_install_from_lockfile(
    engine: *mut EngineHandle,
    directory: *const c_char,
    load: i32,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let directory = read_utf8(directory, "directory")?.to_string();
        runtime().block_on(engine::install_from_lockfile(handle, directory, load != 0))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_source_config_schema(
    engine: *mut EngineHandle,
    kind: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        runtime().block_on(engine::source_config_schema(handle, kind))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_reaction_config_schema(
    engine: *mut EngineHandle,
    kind: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        runtime().block_on(engine::reaction_config_schema(handle, kind))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_bootstrap_config_schema(
    engine: *mut EngineHandle,
    kind: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        runtime().block_on(engine::bootstrap_config_schema(handle, kind))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_secret_store_config_schema(
    engine: *mut EngineHandle,
    kind: *const c_char,
) -> *mut c_char {
    catch_string(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        runtime().block_on(engine::secret_store_config_schema(handle, kind))
    })
}

#[no_mangle]
pub unsafe extern "C" fn drasi_engine_use_secret_store(
    engine: *mut EngineHandle,
    kind: *const c_char,
    config_json: *const c_char,
) -> i32 {
    catch_status(|| {
        let handle = handle_ref(engine)?;
        let kind = read_utf8(kind, "kind")?.to_string();
        let config_json = read_utf8(config_json, "config_json")?;
        runtime().block_on(engine::use_secret_store(handle, kind, config_json))
    })
}

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

//! Host version introspection used to match plugins.

pub const DRASI_CORE_VERSION: &str = "0.5.7";
pub const DRASI_LIB_VERSION: &str = "0.8.9";
pub const DRASI_SDK_VERSION: &str = "0.10.0";

pub fn ffi_sdk_version() -> &'static str {
    drasi_plugin_sdk::ffi::metadata::FFI_SDK_VERSION
}

pub fn target_triple() -> &'static str {
    drasi_plugin_sdk::ffi::metadata::TARGET_TRIPLE
}

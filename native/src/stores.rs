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

//! Optional backing stores and credentials supplied when creating an engine.

use std::collections::HashMap;
use std::sync::Arc;

use drasi_lib::builder::DrasiLibBuilder;
use drasi_lib::identity::{ApplicationIdentityProvider, Credentials, PasswordIdentityProvider};
use drasi_lib::secret_store::MemorySecretStoreProvider;
use serde::Deserialize;
use serde_json::Value;

use crate::error::{ErrorCode, FfiError, FfiResult};

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateOptions {
    #[serde(default)]
    pub secrets: HashMap<String, String>,
    #[serde(alias = "state_store")]
    pub state_store: Option<StateStoreJson>,
    #[serde(alias = "index_store")]
    pub index_store: Option<Value>,
    pub identity: Option<Value>,
    #[serde(alias = "plugins_dir")]
    pub plugins_dir: Option<String>,
}

#[derive(Deserialize)]
pub struct StateStoreJson {
    pub kind: String,
    pub path: Option<String>,
}

pub enum Identity {
    Password { username: String, password: String },
    Token { username: String, token: String },
    Plugin { kind: String, config: Value },
}

impl CreateOptions {
    pub fn parse(json: Option<&str>) -> FfiResult<Self> {
        match json {
            None | Some("") => Ok(Self::default()),
            Some(raw) => serde_json::from_str(raw).map_err(|err| {
                FfiError::config(format!("create options are not valid JSON: {err}"))
            }),
        }
    }

    pub fn has_state_store(&self) -> bool {
        self.state_store.is_some()
    }

    pub fn identity_plugin(&self) -> FfiResult<Option<(String, Value)>> {
        Ok(match self.parse_identity()? {
            Some(Identity::Plugin { kind, config }) => Some((kind, config)),
            _ => None,
        })
    }

    pub fn parse_identity(&self) -> FfiResult<Option<Identity>> {
        let Some(value) = &self.identity else {
            return Ok(None);
        };
        let obj = value
            .as_object()
            .ok_or_else(|| FfiError::config("'identity' must be an object"))?;
        let kind = obj.get("kind").and_then(Value::as_str).ok_or_else(|| {
            FfiError::new(
                ErrorCode::IdentityKindRequired,
                "'identity' requires a 'kind'",
            )
        })?;
        Ok(Some(match kind {
            "password" => Identity::Password {
                username: required_str(obj, "username", ErrorCode::IdentityConfigInvalid)?,
                password: required_str(obj, "password", ErrorCode::IdentityConfigInvalid)?,
            },
            "token" => Identity::Token {
                username: obj
                    .get("username")
                    .and_then(Value::as_str)
                    .unwrap_or("")
                    .to_string(),
                token: required_str(obj, "token", ErrorCode::IdentityConfigInvalid)?,
            },
            other => {
                let mut config = value.clone();
                if let Some(map) = config.as_object_mut() {
                    map.remove("kind");
                }
                Identity::Plugin {
                    kind: other.to_string(),
                    config,
                }
            }
        }))
    }

    pub async fn apply(
        mut self,
        mut builder: DrasiLibBuilder,
        plugin_identity: Option<Arc<dyn drasi_lib::identity::IdentityProvider>>,
    ) -> FfiResult<(DrasiLibBuilder, HashMap<String, String>)> {
        let secrets = std::mem::take(&mut self.secrets);
        let mut store = MemorySecretStoreProvider::new();
        for (name, value) in &secrets {
            store = store.with_secret(name.clone(), value.clone());
        }
        builder = builder.with_secret_store_provider(Arc::new(store));

        if let Some(ref state_store) = self.state_store {
            if state_store.kind != "redb" {
                return Err(FfiError::new(
                    ErrorCode::UnknownStateStoreKind,
                    format!(
                        "unknown state store kind '{}', expected 'redb'",
                        state_store.kind
                    ),
                ));
            }
            let path = state_store.path.clone().ok_or_else(|| {
                FfiError::new(
                    ErrorCode::StateStorePathRequired,
                    "a redb state store requires a 'path'",
                )
            })?;
            let provider =
                drasi_state_store_redb::RedbStateStoreProvider::new(&path).map_err(|err| {
                    FfiError::new(
                        ErrorCode::ConfigInvalid,
                        format!("could not open the redb state store at '{path}': {err}"),
                    )
                })?;
            builder = builder.with_state_store_provider(Arc::new(provider));
        }

        let identity = self.parse_identity()?;
        if let Some(index_store) = self.index_store.take() {
            builder = apply_index_store(builder, index_store).await?;
        }

        if let Some(identity) = identity {
            let provider: Arc<dyn drasi_lib::identity::IdentityProvider> = match identity {
                Identity::Password { username, password } => {
                    Arc::new(PasswordIdentityProvider::new(username, password))
                }
                Identity::Token { username, token } => {
                    Arc::new(ApplicationIdentityProvider::new_sync(move |_| {
                        Ok(Credentials::Token {
                            username: username.clone(),
                            token: token.clone(),
                        })
                    }))
                }
                Identity::Plugin { kind, .. } => plugin_identity.ok_or_else(|| {
                    FfiError::new(
                        ErrorCode::UnknownIdentityKind,
                        format!(
                            "no identity plugin registered for kind '{kind}'; \
                             pass pluginsDir with the plugin in it"
                        ),
                    )
                })?,
            };
            builder = builder.with_identity_provider(provider);
        }

        Ok((builder, secrets))
    }
}

async fn apply_index_store(
    builder: DrasiLibBuilder,
    mut index_store: Value,
) -> FfiResult<DrasiLibBuilder> {
    use drasi_index_rocksdb::RocksDbIndexDescriptor;
    use drasi_plugin_sdk::descriptor::IndexBackendPluginDescriptor;

    let obj = index_store
        .as_object_mut()
        .ok_or_else(|| FfiError::config("'indexStore' must be an object"))?;
    let kind = obj
        .get("kind")
        .and_then(Value::as_str)
        .ok_or_else(|| {
            FfiError::new(
                ErrorCode::UnknownIndexStoreKind,
                "'indexStore' requires a 'kind'",
            )
        })?
        .to_string();
    if kind != "rocksdb" {
        return Err(FfiError::new(
            ErrorCode::UnknownIndexStoreKind,
            format!("unknown index store kind '{kind}', expected 'rocksdb'"),
        ));
    }
    if obj.get("path").and_then(Value::as_str).is_none() {
        return Err(FfiError::new(
            ErrorCode::IndexStorePathRequired,
            "a rocksdb index store requires a 'path'",
        ));
    }
    obj.remove("kind");
    rename(obj, "enable_archive", "enableArchive");
    rename(obj, "direct_io", "directIo");

    let provider = RocksDbIndexDescriptor
        .create_index_backend(&index_store)
        .await
        .map_err(|err| {
            FfiError::new(
                ErrorCode::ConfigInvalid,
                format!("could not open the RocksDB index store: {err}"),
            )
        })?;
    Ok(builder.with_default_index_provider("rocksdb", provider))
}

fn rename(fields: &mut serde_json::Map<String, Value>, from: &str, to: &str) {
    if let Some(value) = fields.remove(from) {
        fields.entry(to.to_string()).or_insert(value);
    }
}

fn required_str(
    obj: &serde_json::Map<String, Value>,
    key: &str,
    code: ErrorCode,
) -> FfiResult<String> {
    obj.get(key)
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| FfiError::new(code, format!("'{key}' is required")))
}

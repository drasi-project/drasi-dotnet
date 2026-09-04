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

namespace Drasi;

/// <summary>
/// Stable, machine-readable failure codes. Branch on
/// <see cref="DrasiException.Code"/> rather than message text.
/// </summary>
public static class DrasiErrorCodes
{
    public const string UnknownSourceKind = "UNKNOWN_SOURCE_KIND";
    public const string UnknownReactionKind = "UNKNOWN_REACTION_KIND";
    public const string UnknownBootstrapKind = "UNKNOWN_BOOTSTRAP_KIND";
    public const string UnknownSecretStoreKind = "UNKNOWN_SECRET_STORE_KIND";
    public const string BootstrapKindRequired = "BOOTSTRAP_KIND_REQUIRED";
    public const string NoCsharpSource = "NO_CSHARP_SOURCE";
    public const string ChangeNotObject = "CHANGE_NOT_OBJECT";
    public const string ChangeOpRequired = "CHANGE_OP_REQUIRED";
    public const string ChangeIdRequired = "CHANGE_ID_REQUIRED";
    public const string RelationRequiresBothEnds = "RELATION_REQUIRES_BOTH_ENDS";
    public const string UnknownChangeOp = "UNKNOWN_CHANGE_OP";
    public const string StateStorePathRequired = "STATE_STORE_PATH_REQUIRED";
    public const string UnknownStateStoreKind = "UNKNOWN_STATE_STORE_KIND";
    public const string IndexStorePathRequired = "INDEX_STORE_PATH_REQUIRED";
    public const string UnknownIndexStoreKind = "UNKNOWN_INDEX_STORE_KIND";
    public const string IdentityKindRequired = "IDENTITY_KIND_REQUIRED";
    public const string UnknownIdentityKind = "UNKNOWN_IDENTITY_KIND";
    public const string IdentityConfigInvalid = "IDENTITY_CONFIG_INVALID";
    public const string DurableRequiresStateStore = "DURABLE_REQUIRES_STATE_STORE";
    public const string UnknownQueryLanguage = "UNKNOWN_QUERY_LANGUAGE";
    public const string ConfigInvalid = "CONFIG_INVALID";
    public const string PluginSignatureInvalid = "PLUGIN_SIGNATURE_INVALID";
    public const string PluginIncompatible = "PLUGIN_INCOMPATIBLE";
    public const string PluginNotFound = "PLUGIN_NOT_FOUND";
    public const string StreamLagged = "STREAM_LAGGED";
    public const string EngineClosed = "ENGINE_CLOSED";
    public const string EngineFailure = "ENGINE_FAILURE";

    /// <summary>Every code the native host may report.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
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
    ];
}

/// <summary>Base class for every error raised by Drasi.</summary>
public class DrasiException : Exception
{
    public DrasiException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Stable code from <see cref="DrasiErrorCodes"/>.</summary>
    public string Code { get; }

    internal static DrasiException FromCode(string code, string message) => code switch
    {
        DrasiErrorCodes.UnknownSourceKind
            or DrasiErrorCodes.UnknownReactionKind
            or DrasiErrorCodes.UnknownBootstrapKind
            or DrasiErrorCodes.UnknownSecretStoreKind
            or DrasiErrorCodes.UnknownStateStoreKind
            or DrasiErrorCodes.UnknownIndexStoreKind
            or DrasiErrorCodes.UnknownIdentityKind
            or DrasiErrorCodes.UnknownQueryLanguage
            or DrasiErrorCodes.UnknownChangeOp => new UnknownKindException(code, message),

        DrasiErrorCodes.BootstrapKindRequired
            or DrasiErrorCodes.StateStorePathRequired
            or DrasiErrorCodes.IndexStorePathRequired
            or DrasiErrorCodes.IdentityKindRequired
            or DrasiErrorCodes.IdentityConfigInvalid
            or DrasiErrorCodes.DurableRequiresStateStore
            or DrasiErrorCodes.ConfigInvalid => new ConfigException(code, message),

        DrasiErrorCodes.NoCsharpSource
            or DrasiErrorCodes.ChangeNotObject
            or DrasiErrorCodes.ChangeOpRequired
            or DrasiErrorCodes.ChangeIdRequired
            or DrasiErrorCodes.RelationRequiresBothEnds => new SourceException(code, message),

        DrasiErrorCodes.PluginSignatureInvalid => new PluginSignatureException(code, message),
        DrasiErrorCodes.PluginIncompatible => new PluginCompatibilityException(code, message),
        DrasiErrorCodes.PluginNotFound => new PluginNotFoundException(code, message),
        DrasiErrorCodes.StreamLagged => new StreamLaggedException(code, message),
        _ => new DrasiException(code, message),
    };
}

/// <summary>Invalid configuration.</summary>
public class ConfigException : DrasiException
{
    public ConfigException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>A source, reaction, bootstrap, store or language kind is not registered.</summary>
public class UnknownKindException : ConfigException
{
    public UnknownKindException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>A change could not be pushed into a C#-defined source.</summary>
public class SourceException : DrasiException
{
    public SourceException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>A stream dropped items because they were not consumed quickly enough.</summary>
public class StreamLaggedException : DrasiException
{
    public StreamLaggedException(string code, string message) : base(code, message)
    {
    }

    public StreamLaggedException(ulong dropped) : base(
        DrasiErrorCodes.StreamLagged,
        $"stream dropped {dropped} item(s) because they were not consumed quickly enough")
    {
        Dropped = dropped;
    }

    public ulong Dropped { get; }
}

/// <summary>A plugin could not be used.</summary>
public class PluginException : DrasiException
{
    public PluginException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>No plugin matched the requested reference.</summary>
public class PluginNotFoundException : PluginException
{
    public PluginNotFoundException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>A plugin is not compatible with this host.</summary>
public class PluginCompatibilityException : PluginException
{
    public PluginCompatibilityException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>A plugin's signature could not be verified.</summary>
public class PluginSignatureException : PluginException
{
    public PluginSignatureException(string code, string message) : base(code, message)
    {
    }
}

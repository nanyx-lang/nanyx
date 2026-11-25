namespace NyxCompiler

open Parser.Program.DataValues

/// The settings a program gives its host, in its manifest's `host`: `host = (package = "nanyx.http.host", version = ...,
/// settings = (port = 8080))`. A host declares the settings it takes, each with its default (see `Host.Settings`), so a
/// setting it doesn't take, or one of another kind than its default, is an error at the setting.
module HostSettings =
    /// Whether `value` is of the same kind as `default`: both whole numbers, both strings, and so on.
    let private sameKind (value: DataValue) (default': DataValue) =
        match value.Data, default'.Data with
        | DataInt _, DataInt _
        | DataFloat _, DataFloat _
        | DataString _, DataString _
        | DataBool _, DataBool _
        | DataTag _, DataTag _
        | DataRecord _, DataRecord _
        | DataList _, DataList _ -> true
        | _ -> false

    /// The errors in the settings `entry` gives `host`.
    let errors (host: Host) (entry: HostEntry) : Diagnostic list =
        let taken = host.Settings |> List.map _.Name |> String.concat ", "
        entry.Settings
        |> List.choose (fun setting ->
            match host.Settings |> List.tryFind (fun declared -> declared.Name = setting.Name) with
            | None when host.Settings.IsEmpty ->
                Some(Diagnostics.at DiagnosticCodes.UnknownManifestField setting.NameSpan $"The host '{host.Package}' takes no settings" |> Diagnostics.about setting.Name)
            | None ->
                Some(Diagnostics.at DiagnosticCodes.UnknownManifestField setting.NameSpan $"The host '{host.Package}' takes no setting '{setting.Name}': it takes {taken}" |> Diagnostics.about setting.Name)
            | Some declared when not (sameKind setting.Value declared.Value) ->
                Some(
                    Diagnostics.at
                        DiagnosticCodes.InvalidManifestValue
                        setting.Value.Location
                        $"The host's setting '{setting.Name}' is {DataValue.describe declared.Value} by default, so it can't be {DataValue.describe setting.Value}"
                )
            | Some _ -> None)

    /// The value of the setting `name` that `entry`, if there is one, gives `host`, or else the host's default; none when
    /// the host doesn't take it.
    let value (host: Host) (entry: HostEntry option) (name: string) : DataValue option =
        entry
        |> Option.bind (fun entry -> entry.Settings |> List.tryFind (fun setting -> setting.Name = name))
        |> Option.orElse (host.Settings |> List.tryFind (fun setting -> setting.Name = name))
        |> Option.map _.Value

    /// The port the HTTP host's `port` setting gives `host`, which `entry` names, if there is one: 3000 when it has
    /// none that is a whole number in range.
    let port (host: Host option) (entry: HostEntry option) : int =
        match host |> Option.bind (fun host -> value host (entry |> Option.filter (fun entry -> entry.Package = host.Package)) "port") with
        | Some { Data = DataInt port } when port > 0I && port < 65536I -> int port
        | _ -> 3000

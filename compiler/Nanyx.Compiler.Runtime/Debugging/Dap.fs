namespace Nanyx.Runtime.Debugging

open System
open System.IO
open System.Text
open System.Text.Json.Nodes

/// The Debug Adapter Protocol's wire format (see https://microsoft.github.io/debug-adapter-protocol/overview): each
/// message a JSON object after a `Content-Length` header and a blank line. A client sends requests, and the adapter
/// answers each with a response and sends events of its own.
module Dap =
    /// The next message on `input`, or none once it has ended.
    let read (input: Stream) : JsonObject option =
        // The header, a line at a time, up to the blank line that ends it.
        let line () =
            let bytes = ResizeArray<byte>()
            let mutable next = input.ReadByte()
            while next <> -1 && next <> int '\n' do
                if next <> int '\r' then bytes.Add(byte next)
                next <- input.ReadByte()
            if next = -1 && bytes.Count = 0 then None else Some(Encoding.ASCII.GetString(bytes.ToArray()))
        let rec header (length: int option) =
            match line () with
            | None -> None
            | Some "" -> length
            | Some text when text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) ->
                header (Some(int (text.Substring("Content-Length:".Length).Trim())))
            | Some _ -> header length
        match header None with
        | None -> None
        | Some length ->
            let body = Array.zeroCreate<byte> length
            let mutable read = 0
            while read < length do
                let got = input.Read(body, read, length - read)
                if got = 0 then raise (EndOfStreamException "The client's message ended early")
                read <- read + got
            Some(JsonNode.Parse(body).AsObject())

    /// Writes `message` to `output`, framed, and flushes it.
    let write (output: Stream) (message: JsonObject) =
        let body = Encoding.UTF8.GetBytes(message.ToJsonString())
        let header = Encoding.ASCII.GetBytes $"Content-Length: {body.Length}\r\n\r\n"
        lock output (fun () ->
            output.Write(header, 0, header.Length)
            output.Write(body, 0, body.Length)
            output.Flush())

    /// A JSON object of `fields`.
    let object (fields: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in fields do
            result.[name] <- value
        result

    /// A JSON array of `items`.
    let array (items: JsonNode seq) =
        let result = JsonArray()
        for item in items do
            result.Add item
        result

    let string (value: string) : JsonNode = JsonValue.Create value
    let number (value: int) : JsonNode = JsonValue.Create value
    let boolean (value: bool) : JsonNode = JsonValue.Create value

    /// The string field `name` of `node`, if it has one.
    let text (name: string) (node: JsonNode) =
        match node with
        | null -> None
        | node ->
            match node.[name] with
            | null -> None
            | value -> Some(value.GetValue<string>())

    /// The number field `name` of `node`, if it has one.
    let integer (name: string) (node: JsonNode) =
        match node with
        | null -> None
        | node ->
            match node.[name] with
            | null -> None
            | value -> Some(value.GetValue<int>())

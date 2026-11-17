namespace Nanyx.Runtime

open System
open System.Net.Http
open System.Text
open System.Threading
open Wasmtime

/// The runtime's own import for outbound HTTP, `nanyx:http`, on which the console host implements `Http` (see the
/// Foreign functions page of the docs). WASI preview 1 has no networking, so until the backend emits components and
/// can use `wasi:http`, a program makes requests through these two functions, backed by .NET's `HttpClient`:
///
/// - `send(request, length, kind) -> i32` makes the request written in the `length` bytes at `request`, blocking until
///   it is answered, keeps the answer, writes what kind of answer it is at `kind` (see `Answer`), and gives its length
///   in bytes;
/// - `receive(destination)` copies the kept answer to `destination`, where the program has made room for it.
///
/// A request is written as lines, each ended by `\n`: the method, the URL, the timeout in milliseconds, the number of
/// headers, then each header's name and value on lines of their own; the body follows, as it is. A response is written
/// in the same way: the status, the number of headers, each header's name (in lower case) and value, then the body.
module HttpImport =
    /// The import module's name.
    [<Literal>]
    let moduleName = "nanyx:http"

    /// What kind of answer `send` kept, as it writes it for the program: a response, or why there is none. With
    /// `ConnectionFailed` and `Other` the answer is a message saying why.
    type Answer =
        | Response = 0
        | Timeout = 1
        | ConnectionFailed = 2
        | InvalidUrl = 3
        | Other = 4

    /// One client for every request the process makes, as `HttpClient` is meant to be used. Each request has a timeout
    /// of its own.
    let private client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

    /// Reads `bytes` a line at a time, each ended by `\n`, and then what is left.
    type private Lines(bytes: byte array) =
        let mutable position = 0

        /// The next line, without its `\n`.
        member _.Next() =
            let start = position
            let stop = Array.IndexOf(bytes, byte '\n', start)
            let stop = if stop < 0 then bytes.Length else stop
            position <- min bytes.Length (stop + 1)
            Encoding.UTF8.GetString(bytes, start, stop - start)

        /// The headers that follow: how many there are, then each one's name and value.
        member lines.Headers() = [ for _ in 1 .. int (lines.Next()) -> let name = lines.Next() in name, lines.Next() ]

        /// What is left after the lines read.
        member _.Rest = bytes.[position..]

    /// `headers`, how many there are and then each one's name and value, each on a line of its own, after `text`.
    let private appendHeaders (text: StringBuilder) (headers: (string * string) list) =
        text.Append(headers.Length).Append('\n') |> ignore
        for name, value in headers do
            text.Append(name).Append('\n').Append(value).Append('\n') |> ignore

    /// A request to the program, as its host reads it (see the module's comment): with no timeout, since it is the
    /// program that answers it.
    let internal writtenRequest (methodName: string) (url: string) (headers: (string * string) list) (body: byte array) =
        let text = StringBuilder()
        text.Append(methodName).Append('\n').Append(url).Append("\n0\n") |> ignore
        appendHeaders text headers
        Array.append (Encoding.UTF8.GetBytes(text.ToString())) body

    /// The status, headers and body of the response written in `bytes`, or none when it isn't one.
    let internal responseOf (bytes: byte array) : (int * (string * string) list * byte array) option =
        try
            let lines = Lines bytes
            let status = int (lines.Next())
            let headers = lines.Headers()
            Some(status, headers, lines.Rest)
        with :? FormatException -> None

    /// The request written in `bytes` (see the module's comment), with its timeout, or why it isn't one.
    let private requestOf (bytes: byte array) : Result<HttpRequestMessage * TimeSpan, Answer * string> =
        let lines = Lines bytes
        let methodName = lines.Next()
        let url = lines.Next()
        let timeout = TimeSpan.FromMilliseconds(float (int (lines.Next())))
        let headers = lines.Headers()
        let body = lines.Rest
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
            let request = new HttpRequestMessage(HttpMethod(methodName), uri)
            if body.Length > 0 || methodName = "POST" || methodName = "PUT" || methodName = "PATCH" then
                request.Content <- new ByteArrayContent(body)
            for name, value in headers do
                // Some headers, such as `Content-Type`, are the content's rather than the request's.
                if not (request.Headers.TryAddWithoutValidation(name, value)) && not (isNull request.Content) then
                    request.Content.Headers.TryAddWithoutValidation(name, value) |> ignore
            Ok(request, timeout)
        | _ -> Error(Answer.InvalidUrl, url)

    /// `response` written as the program reads it (see the module's comment).
    let private written (response: HttpResponseMessage) (body: byte array) =
        let headers =
            Seq.append response.Headers response.Content.Headers
            |> Seq.map (fun header -> header.Key.ToLowerInvariant(), String.Join(", ", header.Value))
            |> List.ofSeq
        let text = StringBuilder()
        text.Append(int response.StatusCode).Append('\n') |> ignore
        appendHeaders text headers
        Array.append (Encoding.UTF8.GetBytes(text.ToString())) body

    /// Makes the request written in `bytes`, and gives what kind of answer it had, with the answer.
    let private answer (bytes: byte array) : Answer * byte array =
        match requestOf bytes with
        | Error(kind, message) -> kind, Encoding.UTF8.GetBytes message
        | Ok(request, timeout) ->
            use request = request
            use cancel = new CancellationTokenSource(timeout)
            try
                use response = client.SendAsync(request, cancel.Token).GetAwaiter().GetResult()
                let body = response.Content.ReadAsByteArrayAsync(cancel.Token).GetAwaiter().GetResult()
                Answer.Response, written response body
            with
            // A timeout that ends the request while it connects can fail it as a connection that failed.
            | _ when cancel.IsCancellationRequested -> Answer.Timeout, [||]
            | :? OperationCanceledException -> Answer.Timeout, [||]
            | :? HttpRequestException as ex -> Answer.ConnectionFailed, Encoding.UTF8.GetBytes ex.Message
            | ex -> Answer.Other, Encoding.UTF8.GetBytes ex.Message

    /// Defines `send` and `receive` on `linker`, for one store: the answer `send` keeps is this store's.
    let define (linker: Linker) =
        let kept = ref [||]
        linker.DefineFunction(
            moduleName,
            "send",
            CallerFunc<int, int, int, int>(fun caller request length kind ->
                let memory = caller.GetMemory "memory"
                let answerKind, answer = answer (memory.GetSpan(int64 request, length).ToArray())
                kept.Value <- answer
                memory.WriteInt32(int64 kind, int answerKind)
                answer.Length))
        linker.DefineFunction(
            moduleName,
            "receive",
            CallerAction<int>(fun caller destination ->
                let memory = caller.GetMemory "memory"
                kept.Value.AsSpan().CopyTo(memory.GetSpan(int64 destination, kept.Value.Length))
                kept.Value <- [||]))

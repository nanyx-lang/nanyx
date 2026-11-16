namespace NyxCompiler

open System
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Threading.Tasks

/// Fetching bytes over HTTPS and unpacking a zip into the package cache: what the package index (`PackageIndex`) and
/// GitHub (`GitHub`) are downloaded with.
module Downloads =
    /// Whether `location` is an address to download from, rather than a path on disk.
    let isAddress (location: string) =
        location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)

    let private client =
        lazy
            (let client = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
             client.DefaultRequestHeaders.UserAgent.ParseAdd $"nanyx/{Toolchain.version}"
             client)

    /// The bytes at `address`, sent with the headers `headers`; none when there is nothing there (404). Or why they
    /// can't be had.
    let get (headers: (string * string) list) (address: string) : Result<byte array option, string> =
        try
            use request = new HttpRequestMessage(HttpMethod.Get, address)
            for name, value in headers do
                request.Headers.TryAddWithoutValidation(name, value) |> ignore
            use response = client.Value.Send request
            if response.IsSuccessStatusCode then
                use stream = response.Content.ReadAsStream()
                use bytes = new MemoryStream()
                stream.CopyTo bytes
                Result.Ok(Some(bytes.ToArray()))
            elif response.StatusCode = HttpStatusCode.NotFound then
                Result.Ok None
            else
                Result.Error $"{address} answered {int response.StatusCode} {response.ReasonPhrase}"
        with
        | :? HttpRequestException
        | :? TaskCanceledException as error -> Result.Error $"{address} can't be reached: {error.Message}"

    /// The path of `entry`, a name in a zip, after `prefix`, relative to where it is unpacked; or none if it would land
    /// outside that directory.
    let private relative (prefix: string) (entry: string) =
        let name = entry.Replace('\\', '/')
        if not (name.StartsWith prefix) then
            None
        else
            let rest = name.Substring prefix.Length
            let parts = rest.Split '/'
            if rest = "" || Path.IsPathRooted rest || rest.StartsWith "/" || parts |> Array.exists (fun part -> part = ".." || part.Contains ':') then None
            else Some rest

    /// The one folder every file of a zip is in, `acme-1f8ae8b/`, if they are all in one; `names` are the files' names.
    let singleFolder (names: string list) : string option =
        match names |> List.map (fun name -> name.Split('/')[0]) |> List.distinct with
        | [ folder ] when names |> List.forall (fun name -> name.Contains '/') -> Some(folder + "/")
        | _ -> None

    /// Unpacks the files of the zip `bytes` into `directory`, which must not exist yet: each file whose name starts
    /// with the prefix `prefixOf` finds from the files' names, at its name after the prefix. A file that would land
    /// outside `directory` is an error. It is unpacked beside `directory` first and moved there, so it is never seen
    /// half unpacked; if `directory` is there by then, another download made it, and it is kept, since what is in the
    /// cache is never changed.
    let unzip (prefixOf: string list -> Result<string, string>) (bytes: byte array) (directory: string) : Result<unit, string> =
        try
            use zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)
            let files = zip.Entries |> Seq.filter (fun entry -> not (entry.FullName.EndsWith "/")) |> List.ofSeq
            prefixOf (files |> List.map _.FullName)
            |> Result.bind (fun prefix ->
                match files |> List.tryFind (fun entry -> (relative prefix entry.FullName).IsNone) with
                | Some outside -> Result.Error $"its entry {outside.FullName} would be unpacked outside its directory"
                | None ->
                    let partial = $"{directory}.partial-{Guid.NewGuid():N}"
                    try
                        for entry in files do
                            let target = Path.Combine(partial, (relative prefix entry.FullName).Value)
                            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                            entry.ExtractToFile target
                        Directory.CreateDirectory partial |> ignore
                        Directory.CreateDirectory(Path.GetDirectoryName directory) |> ignore
                        if not (Directory.Exists directory) then
                            Directory.Move(partial, directory)
                        Result.Ok()
                    finally
                        if Directory.Exists partial then
                            Directory.Delete(partial, true))
        with :? InvalidDataException as error ->
            Result.Error $"it is not a zip: {error.Message}"

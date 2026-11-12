using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Nanyx.LanguageServer;

/// <summary>
/// Compiles a changed document once the user pauses, rather than on every change. A change records the new text and
/// returns at once; the text is compiled when the document has gone unchanged for the settings' compile delay, and only
/// the latest text of each document is. A compile cannot be stopped once it has started, so a change that arrives
/// during one waits for it: that compile's result, which is already out of date, is neither recorded nor published,
/// and the latest text is compiled next. Until then, requests read the last text that was typed (see
/// <see cref="OpenDocument.LastGood"/>).
/// </summary>
public sealed class CompileScheduler : IDisposable
{
    /// <summary>What is waiting to be compiled for one document, and whether a compile of it is running.</summary>
    private sealed class Pending(Timer timer)
    {
        public Timer Timer { get; } = timer;
        public string Text { get; set; } = string.Empty;
        public int? Version { get; set; }

        /// <summary>Counts the changes, so a compile can tell whether a later one has replaced its text.</summary>
        public int Generation { get; set; }

        /// <summary>Whether the latest text has waited out the delay, and is to be compiled.</summary>
        public bool Due { get; set; }

        public bool Running { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<DocumentUri, Pending> _pending = new();
    private readonly DocumentStore _documents;
    private readonly DiagnosticsPublisher _diagnostics;
    private readonly TimeSpan _delay;

    public CompileScheduler(DocumentStore documents, DiagnosticsPublisher diagnostics, ServerSettings settings)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _delay = settings.CompileDelay ?? ServerSettings.DefaultCompileDelay;
    }

    /// <summary>
    /// Records a document's new text, and compiles it, and publishes its diagnostics, once the document has gone
    /// unchanged for the delay.
    /// </summary>
    public void Changed(DocumentUri uri, string text, int? version)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(uri, out var pending))
            {
                pending = new Pending(new Timer(_ => Elapsed(uri)));
                _pending[uri] = pending;
            }

            pending.Text = text;
            pending.Version = version;
            pending.Generation++;
            pending.Due = false;
            pending.Timer.Change(_delay, Timeout.InfiniteTimeSpan);
            _documents.Edit(uri, text);
        }
    }

    /// <summary>
    /// Compiles a document's text now, records it and publishes its diagnostics, as when it is opened: any change
    /// still waiting to be compiled is dropped. A change that arrives during the compile wins over it.
    /// </summary>
    public void CompileNow(DocumentUri uri, string text, int? version)
    {
        lock (_lock)
        {
            Forget(uri);
        }

        var compiled = _documents.Compile(uri, text);

        lock (_lock)
        {
            if (!_pending.ContainsKey(uri))
            {
                _diagnostics.Publish(uri, _documents.Record(uri, compiled), version);
            }
        }
    }

    /// <summary>Stops compiling a document the client has closed.</summary>
    public void Closed(DocumentUri uri)
    {
        lock (_lock)
        {
            Forget(uri);
        }
    }

    private void Forget(DocumentUri uri)
    {
        if (_pending.Remove(uri, out var pending))
        {
            pending.Timer.Dispose();
            // A compile still running finds its document gone, and records and publishes nothing.
            pending.Generation++;
        }
    }

    private void Elapsed(DocumentUri uri)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(uri, out var pending))
            {
                return;
            }

            pending.Due = true;
            if (pending.Running)
            {
                // The running compile starts this one when it finishes.
                return;
            }

            pending.Running = true;
        }

        _ = Task.Run(() => Run(uri));
    }

    /// <summary>Compiles the document's latest text while there is one due, one compile at a time.</summary>
    private void Run(DocumentUri uri)
    {
        while (true)
        {
            string text;
            int? version;
            int generation;
            Pending? pending;
            lock (_lock)
            {
                if (!_pending.TryGetValue(uri, out pending) || !pending.Due)
                {
                    if (pending is not null)
                    {
                        pending.Running = false;
                    }

                    return;
                }

                pending.Due = false;
                text = pending.Text;
                version = pending.Version;
                generation = pending.Generation;
            }

            var document = _documents.Compile(uri, text);

            lock (_lock)
            {
                if (_pending.TryGetValue(uri, out var current) && ReferenceEquals(current, pending) && current.Generation == generation)
                {
                    _diagnostics.Publish(uri, _documents.Record(uri, document), version);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var pending in _pending.Values)
            {
                pending.Timer.Dispose();
            }

            _pending.Clear();
        }
    }
}

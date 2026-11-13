using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary><c>nanyx/infoView</c>, the request behind the extension's info view.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class InfoViewTests : LanguageServerTestBase
{
    [Fact]
    public async Task Info_view_shows_the_expression_and_the_names_in_scope_nearest_first()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-1");

        Open(uri, """
            def greet: string -> string = { name ->
              def shout = name
              shout
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 2, 3);

        var expression = Assert.Single(Section(view, "cursor", "expression"));
        Assert.Equal("shout: string", expression.Text);
        Assert.Equal(new Range(2, 2, 2, 7), expression.Location?.Range);
        Assert.Equal(
            [("shout: string", "local", new Range(1, 6, 1, 11)), ("name: string", "parameter", new Range(0, 32, 0, 36))],
            Section(view, "function", "names").Select(item => (item.Text, item.Detail, item.Location?.Range)));
        Assert.True(view.Scopes[^1].Collapsed);
        var topLevel = Section(view, "module", "names");
        Assert.Equal(("greet: string -> string", "def"), (topLevel[0].Text, topLevel[0].Detail));
        Assert.DoesNotContain(topLevel, item => item.Text.StartsWith("__"));
        Assert.Empty(Section(view, "cursor", "messages"));
    }

    [Fact]
    public async Task Info_view_shows_the_contexts_of_the_signature_and_of_a_use()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-2");

        Open(uri, """
            context Logger = (log: string -> ())
            context Clock = (now: () -> int)
            def quiet: Logger = (log = { s -> () })
            def stamp: [Clock] string -> () = { message ->
              use quiet
              log(message)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var contexts = Section(await InfoViewAt(uri, 5, 4), "function", "contexts");

        Assert.Equal(
            [("Logger", "use", new Range(4, 2, 4, 11)), ("Clock", "signature", new Range(3, 11, 3, 31))],
            contexts.Select(item => (item.Text, item.Detail, item.Location?.Range)));
    }

    [Fact]
    public async Task Info_view_shows_contexts_inferred_for_a_def_and_the_runtime_in_the_module()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-3");

        Open(uri, """
            import nanyx/console

            def hello = { println("hi") }
            println("top")

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var inDef = Assert.Single(Section(await InfoViewAt(uri, 2, 16), "function", "contexts"));
        Assert.Equal(("Console", "inferred", new Range(2, 4, 2, 9)), (inDef.Text, inDef.Detail, inDef.Location?.Range));
        var atTopLevel = Assert.Single(Section(await InfoViewAt(uri, 3, 2), "module", "contexts"));
        Assert.Equal(("Console", "runtime", null), (atTopLevel.Text, atTopLevel.Detail, atTopLevel.Location));
        // A standard-library context shows its doc comment.
        Assert.StartsWith("The terminal: standard output", atTopLevel.Documentation);
    }

    [Fact]
    public async Task Info_view_shows_the_diagnostics_on_the_line()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-4");

        Open(uri, """
            import nanyx/console

            println(missing)

            """.ReplaceLineEndings("\n"));
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var message = Assert.Single(Section(await InfoViewAt(uri, 2, 0), "cursor", "messages"));
        Assert.Equal("error NYX0101", message.Detail);
        Assert.Contains("missing", message.Text);
        Assert.Equal(new Range(2, 8, 2, 15), message.Location?.Range);
        Assert.Empty(Section(await InfoViewAt(uri, 0, 0), "cursor", "messages"));
    }

    [Fact]
    public async Task Info_view_reads_the_last_typed_text_while_the_text_does_not_parse()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-5");

        Open(uri, "def double: int -> int = { n -> n + n }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
        Change(uri, 2, "def double: int -> int = { n -> n + n }\ndef broken = (\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 32);

        Assert.Equal("n: int", Assert.Single(Section(view, "cursor", "expression")).Text);
        Assert.Equal("n: int", Assert.Single(Section(view, "function", "names")).Text);
    }

    [Fact]
    public async Task Info_view_shows_the_signature_of_a_generic_def_beside_its_type_at_the_use()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-6");

        Open(uri, """
            def pair: a, b -> (a, b) = { x, y -> (x, y) }
            def first = { x, y -> x }
            def p = pair("a", #some(1))
            def f = first(1, "b")

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var annotated = Section(await InfoViewAt(uri, 2, 9), "cursor", "expression");
        Assert.Equal(
            [
                ("def pair: a, b -> (a, b)", "declared"),
                ("def pair: string, #some(int) -> (string, #some(int))", "at this use"),
                ("a = string, b = #some(int)", "type variables")
            ],
            annotated.Select(item => (item.Text, item.Detail)));
        Assert.All(annotated, item => Assert.Equal(new Range(0, 4, 0, 8), item.Location?.Range));

        var inferred = Section(await InfoViewAt(uri, 3, 9), "cursor", "expression");
        Assert.Equal(
            [
                ("def first: α, β -> α", "declared"),
                ("def first: int, string -> int", "at this use"),
                ("α = int, β = string", "type variables")
            ],
            inferred.Select(item => (item.Text, item.Detail)));
    }

    [Fact]
    public async Task Info_view_shows_a_def_that_is_not_generic_once()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-7");

        Open(uri, """
            def double: int -> int = { n -> n + n }
            def four = double(2)

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var expression = Assert.Single(Section(await InfoViewAt(uri, 1, 12), "cursor", "expression"));
        Assert.Equal(("def double: int -> int", null), (expression.Text, expression.Detail));
        // The def's own name shows its signature once too.
        Assert.Single(Section(await InfoViewAt(uri, 0, 5), "cursor", "expression"));
    }

    [Fact]
    public async Task Info_view_shows_the_signature_of_a_def_with_a_context_variable()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-8");

        Open(uri, """
            import nanyx/console
            def apply: [c] ([c] a -> b), a -> b = { f, x -> f(x) }
            def n = apply({ x -> x + 1 }, 1)
            def say = { apply({ x -> println(x) }, "hi") }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A context variable that stands for no contexts is not among the bindings.
        Assert.Equal(
            [
                ("def apply: [c] ([c] a -> b), a -> b", "declared"),
                ("def apply: (int -> int), int -> int", "at this use"),
                ("a = int, b = int", "type variables")
            ],
            Section(await InfoViewAt(uri, 2, 10), "cursor", "expression").Select(item => (item.Text, item.Detail)));
        Assert.Equal(
            [
                ("def apply: [c] ([c] a -> b), a -> b", "declared"),
                ("def apply: [Console] ([Console] string -> ()), string -> ()", "at this use"),
                ("c = Console, a = string, b = ()", "type variables")
            ],
            Section(await InfoViewAt(uri, 3, 14), "cursor", "expression").Select(item => (item.Text, item.Detail)));
    }

    [Fact]
    public async Task Info_view_shows_the_signature_of_a_def_from_an_imported_module()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-info-view-");
        try
        {
            var modulePath = Path.Combine(directory.FullName, "mod.nyx");
            File.WriteAllText(modulePath, """
                module mod
                export def wrap: a -> #some(a) | #nothing = { x -> #some(x) }

                """.ReplaceLineEndings("\n"));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, """
                module main
                import mod as m
                def w = m.wrap("hi")

                """.ReplaceLineEndings("\n"));
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            var items = Section(await InfoViewAt(uri, 2, 11), "cursor", "expression");
            Assert.Equal(
                [
                    ("def wrap: a -> #some(a) | #nothing", "declared"),
                    ("def wrap: string -> #some(string) | #nothing", "at this use"),
                    ("a = string", "type variables")
                ],
                items.Select(item => (item.Text, item.Detail)));
            Assert.All(items, item => Assert.Equal(
                new Location { Uri = DocumentUri.FromFileSystemPath(modulePath), Range = new Range(1, 11, 1, 15) },
                item.Location));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Info_view_shows_the_signature_of_a_standard_library_def_with_contexts()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-9");

        Open(uri, """
            import nanyx/math as m
            def biggest = m.max(1, 2)

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [
                ("def max: [Ord(a)] a, a -> a", "declared"),
                ("def max: [Ord(int)] int, int -> int", "at this use"),
                ("a = int", "type variables")
            ],
            Section(await InfoViewAt(uri, 1, 17), "cursor", "expression").Select(item => (item.Text, item.Detail)));
    }

    [Fact]
    public async Task Info_view_shows_the_doc_comments_of_the_names_and_contexts_in_the_module()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("info-view-docs.nyx"));

        Open(uri, """
            --| Writes lines somewhere.
            context Logger = (log: string -> ())
            --| Tells the time.
            context Clock = (now: () -> int)
            --| A logger that writes nothing.

              It drops every line.
            def quiet: Logger = (log = { s -> () })
            --| Stamps `message`, as [`quiet`] logs it.
            def stamp: [Clock] string -> () = { message ->
              --| The message, shouted.
              def loud = message
              use quiet
              log(loud)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 13, 7);

        Assert.Equal("The message, shouted.", Assert.Single(Section(view, "cursor", "expression")).Documentation);
        Assert.Equal(
            [("loud: string", "The message, shouted."), ("message: string", null)],
            Section(view, "function", "names").Select(item => (item.Text, item.Documentation)));
        var stamp = Section(view, "module", "names").Single(item => item.Text.StartsWith("stamp"));
        Assert.Equal($"Stamps `message`, as [`quiet`]({uri}#L8,5) logs it.", stamp.Documentation);
        // A use shows the doc comment of the def it names, and a signature's context the context's own.
        Assert.Equal(
            [("Logger", "A logger that writes nothing.\n\nIt drops every line."), ("Clock", "Tells the time.")],
            Section(view, "function", "contexts").Select(item => (item.Text, item.Documentation)));
    }

    [Fact]
    public async Task Info_view_shows_the_doc_comment_of_a_def_from_an_imported_module()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-info-view-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "mod.nyx"), """
                module mod
                --| Wraps `x` in a `#some`.
                export def wrap: a -> #some(a) | #nothing = { x -> #some(x) }

                """.ReplaceLineEndings("\n"));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, """
                module main
                import mod as m
                def w = m.wrap("hi")

                """.ReplaceLineEndings("\n"));
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            var items = Section(await InfoViewAt(uri, 2, 11), "cursor", "expression");
            Assert.Equal(
                [("declared", "Wraps `x` in a `#some`."), ("at this use", null), ("type variables", null)],
                items.Select(item => (item.Detail, item.Documentation)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Info_view_shows_the_doc_comment_of_a_standard_library_def()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-10");

        Open(uri, """
            import nanyx/math as m
            def biggest = m.max(1, 2)

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var declared = Section(await InfoViewAt(uri, 1, 17), "cursor", "expression")[0];
        Assert.Equal("The larger of `a` and `b`.", declared.Documentation);
    }

    [Fact]
    public async Task Info_view_shows_a_module_and_its_doc_comment_at_its_name()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-13");

        Open(uri, """
            import nanyx/math as m
            def biggest = m.max(1, 2)

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        foreach (var (line, character, range) in new[] { (0, 10, new Range(0, 7, 0, 22)), (1, 14, new Range(1, 14, 1, 15)) })
        {
            var view = await InfoViewAt(uri, line, character);
            var module = Assert.Single(Section(view, "cursor", "expression"));
            Assert.Equal(("module math", "module", range), (module.Text, module.Detail, module.Location?.Range));
            Assert.NotNull(module.Documentation);
            Assert.Equal("At the cursor", view.Scopes[0].Heading);
        }
    }

    [Fact]
    public async Task Info_view_shows_only_the_cursor_and_the_module_at_the_top_level()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-14");

        Open(uri, "def one = 1\none\n");
        await NextPublished(uri);

        var view = await InfoViewAt(uri, 1, 1);

        Assert.Equal([("cursor", "At the cursor"), ("module", "this module")], view.Scopes.Select(scope => (scope.Kind, scope.Heading)));
        Assert.Equal(["names"], view.Scopes[1].Sections.Select(section => section.Id));
    }

    [Fact]
    public async Task Info_view_shows_each_scope_around_a_nested_lambda_innermost_first()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-15");

        Open(uri, """
            context Log = (log: string -> ())
            def apply: [c] ([c] int -> ()), int -> () = { f, n -> f(n) }
            def run: [Log] string -> () = { prefix ->
              def report = { count: int ->
                apply({ x ->
                  def line = "{prefix} {x}"
                  log(line)
                }, count)
              }
              report(2)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 6, 6);

        Assert.Equal(
            [
                ("cursor", "At the cursor", false), ("lambda", "{ x -> … } (line 5)", false), ("function", "def report", false),
                ("function", "def run", true), ("module", "this module", true)
            ],
            view.Scopes.Select(scope => (scope.Kind, scope.Heading, scope.Collapsed)));
        var lambda = view.Scopes[1];
        Assert.Equal(new Range(4, 10, 7, 5), lambda.Location?.Range);
        Assert.Equal(["type", "names"], lambda.Sections.Select(section => section.Id));
        Assert.Equal("[Log] int -> ()", Assert.Single(lambda.Sections[0].Items).Text);
        Assert.Equal([("line: string", "local"), ("x: int", "parameter")], lambda.Sections[1].Items.Select(item => (item.Text, item.Detail)));

        var report = view.Scopes[2];
        Assert.Equal(new Range(3, 6, 3, 12), report.Location?.Range);
        Assert.Equal(("report: [Log] int -> ()", "inferred"), (report.Sections[0].Items[0].Text, report.Sections[0].Items[0].Detail));
        Assert.Equal(["count: int"], Section(view, "function", "names").Select(item => item.Text));

        var run = view.Scopes[3];
        Assert.Equal(
            [
                ("signature", ["run: [Log] string -> ()"]),
                ("names", ["report: [Log] int -> ()", "prefix: string"]),
                ("contexts", ["Log"])
            ],
            run.Sections.Where(section => section.Id != "flow").Select(section => (section.Id, section.Items.Select(item => item.Text).ToArray())));
        Assert.Equal("declared", run.Sections[0].Items[0].Detail);
        Assert.Contains(view.Scopes[^1].Sections[0].Items, item => item.Text.StartsWith("apply:"));
    }

    [Fact]
    public async Task Info_view_gives_a_use_to_the_lambda_it_is_in_and_leaves_a_memory_block_in_its_scope()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-16");

        Open(uri, """
            context Log = (log: string -> ())
            def quiet: Log = (log = { s -> () })
            def apply: [c] ([c] int -> ()), int -> () = { f, n -> f(n) }
            def total: [Log] int -> int = { n ->
              apply({ x ->
                use quiet
                log("x")
              }, n)
              memory {
                mut sum = 0
                sum := sum + n
                sum
              }
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var inLambda = await InfoViewAt(uri, 6, 5);
        Assert.Equal(("Log", "use"), Assert.Single(Section(inLambda, "lambda", "uses")) is var use ? (use.Text, use.Detail) : default);
        Assert.Equal([("Log", "signature")], Section(inLambda, "function", "contexts").Select(item => (item.Text, item.Detail)));

        var inMemory = await InfoViewAt(uri, 10, 5);
        Assert.Equal(["cursor", "function", "module"], inMemory.Scopes.Select(scope => scope.Kind));
        Assert.Equal(["sum: int", "n: int"], Section(inMemory, "function", "names").Select(item => item.Text));
    }

    [Fact]
    public async Task Info_view_shows_where_the_call_at_the_position_finds_each_context()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-11");

        Open(uri, """
            context Show(a) = (show: a -> string)
            context Log = (log: string -> ())
            type Point = (x: int, y: int)
            def describe: [Log & Show(a)] a -> string = { x -> show(x) }
            def showPoint: Show(Point) = (show = { p -> "p" })
            def main: [Log] () -> string = {
              use showPoint
              describe(Point(x = 1, y = 2))
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 7, 4);

        Assert.Equal("This call", view.Scopes[0].Sections.Single(section => section.Id == "call").Title);
        Assert.Equal(
            [("Log", "signature", new Range(5, 11, 5, 14)), ("Show(Point)", "use", new Range(6, 6, 6, 15))],
            Section(view, "cursor", "call").Select(item => (item.Text, item.Detail, item.Location?.Range)));
        Assert.Empty(Section(await InfoViewAt(uri, 4, 5), "cursor", "call"));
    }

    [Fact]
    public async Task Info_view_shows_the_flow_of_contexts_through_the_def_and_highlights_a_use_and_its_calls()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-12");

        Open(uri, """
            context Log = (log: string -> ())
            def note: [Log] string -> () = { s -> log(s) }
            def quiet: Log = (log = { s -> () })
            def main: [Log] () -> () = {
              note("a")
              use quiet
              note("b")
              use quiet
              ()
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 5, 3);
        var flow = Section(view, "function", "flow");

        Assert.Equal("def main", view.Scopes[1].Heading);
        Assert.Equal(
            [("Log", "signature · 1 call", new Range(3, 11, 3, 14)), ("Log", "use · 1 call", new Range(5, 6, 5, 11)), ("Log", "use · unused", new Range(7, 6, 7, 11))],
            flow.Select(item => (item.Text, item.Detail, item.Location?.Range)));
        var served = Assert.Single(flow[1].Children!);
        Assert.Equal(("note", new Range(6, 2, 6, 6)), (served.Text, served.Location?.Range));
        Assert.Equal(
            [("provider", new Range(5, 6, 5, 11)), ("call", new Range(6, 2, 6, 6))],
            view.Highlights.Select(highlight => (highlight.Kind, highlight.Range)));
        Assert.Equal(
            [("provider", new Range(3, 11, 3, 14)), ("call", new Range(4, 2, 4, 6))],
            (await InfoViewAt(uri, 4, 3)).Highlights.Select(highlight => (highlight.Kind, highlight.Range)));
    }
}

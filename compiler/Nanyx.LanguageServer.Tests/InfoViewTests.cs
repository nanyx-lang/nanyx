using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary><c>nanyx/infoView</c>, the request behind the extension's info view.</summary>
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
            def stamp: [Clock] string -> () = { message ->
              use Logger(log = { s -> () })
              log(message)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var contexts = Section(await InfoViewAt(uri, 4, 4), "function", "contexts");

        Assert.Equal(
            [("Logger", "use", new Range(3, 2, 3, 31)), ("Clock", "signature", new Range(2, 11, 2, 31))],
            contexts.Select(item => (item.Text, item.Detail, item.Location?.Range)));
    }

    [Fact]
    public async Task Info_view_shows_contexts_inferred_for_a_def_and_the_runtime_in_the_module()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-3");

        Open(uri, """
            def hello = { println("hi") }
            println("top")

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var inDef = Assert.Single(Section(await InfoViewAt(uri, 0, 16), "function", "contexts"));
        Assert.Equal(("Console", "inferred", new Range(0, 4, 0, 9)), (inDef.Text, inDef.Detail, inDef.Location?.Range));
        // The runtime supplies every context of the host's prelude at the top level.
        var topLevel = Section(await InfoViewAt(uri, 1, 2), "module", "contexts");
        Assert.Equal([("Async", "runtime", null), ("Clock", "runtime", null), ("Console", "runtime", null), ("Env", "runtime", null), ("FileSystem", "runtime", null), ("Http", "runtime", null), ("Random", "runtime", null)], topLevel.Select(item => (item.Text, item.Detail, item.Location)));
        var atTopLevel = topLevel.Single(item => item.Text == "Console");
        // A context of the host's prelude shows its doc comment.
        Assert.StartsWith("The terminal: standard output", atTopLevel.Documentation);
    }

    [Fact]
    public async Task Info_view_shows_the_diagnostics_on_the_line()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-4");

        Open(uri, """
            println(missing)

            """.ReplaceLineEndings("\n"));
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var message = Assert.Single(Section(await InfoViewAt(uri, 0, 0), "cursor", "messages"));
        Assert.Equal("error NYX0101", message.Detail);
        Assert.Contains("missing", message.Text);
        Assert.Equal(new Range(0, 8, 0, 15), message.Location?.Range);
        Assert.Empty(Section(await InfoViewAt(uri, 1, 0), "cursor", "messages"));
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
            Section(await InfoViewAt(uri, 1, 10), "cursor", "expression").Select(item => (item.Text, item.Detail)));
        Assert.Equal(
            [
                ("def apply: [c] ([c] a -> b), a -> b", "declared"),
                ("def apply: [Console] ([Console] string -> ()), string -> ()", "at this use"),
                ("c = [Console], a = string, b = ()", "type variables")
            ],
            Section(await InfoViewAt(uri, 2, 14), "cursor", "expression").Select(item => (item.Text, item.Detail)));
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
            def drop: string -> () = { s -> () }
            --| Stamps `message`, as [`drop`] logs it.
            def stamp: [Clock] string -> () = { message ->
              --| The message, shouted.
              def loud = message
              use Logger(log = drop)
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
        Assert.Equal($"Stamps `message`, as [`drop`]({uri}#L8,5) logs it.", stamp.Documentation);
        // A use, and a signature's context, show the context's own doc comment.
        Assert.Equal(
            [("Logger", "Writes lines somewhere."), ("Clock", "Tells the time.")],
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
        // The module's contexts are the ones the runtime supplies, from the host's prelude.
        Assert.Equal(["names", "contexts"], view.Scopes[1].Sections.Select(section => section.Id));
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
            def apply: [c] ([c] int -> ()), int -> () = { f, n -> f(n) }
            def total: [Log] int -> int = { n ->
              apply({ x ->
                use Log(log = { s -> () })
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

        var inLambda = await InfoViewAt(uri, 5, 5);
        Assert.Equal(("Log", "use"), Assert.Single(Section(inLambda, "lambda", "uses")) is var use ? (use.Text, use.Detail) : default);
        Assert.Equal([("Log", "signature")], Section(inLambda, "function", "contexts").Select(item => (item.Text, item.Detail)));

        var inMemory = await InfoViewAt(uri, 9, 5);
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
            def describe: [Log, Show(a)] a -> string = { x -> show(x) }
            def main: [Log] () -> string = {
              use Show(Point)(show = { p -> "p" })
              describe(Point(x = 1, y = 2))
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 6, 4);

        Assert.Equal("This call", view.Scopes[0].Sections.Single(section => section.Id == "call").Title);
        Assert.Equal(
            [("Log", "signature", new Range(4, 11, 4, 14)), ("Show(Point)", "use", new Range(5, 6, 5, 38))],
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
            def main: [Log] () -> () = {
              note("a")
              use Log(log = { s -> () })
              note("b")
              use Log(log = { s -> () })
              ()
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 4, 3);
        var flow = Section(view, "function", "flow");

        Assert.Equal("def main", view.Scopes[1].Heading);
        Assert.Equal(
            [("Log", "signature · 1 call", new Range(2, 11, 2, 14)), ("Log", "use · 1 call", new Range(4, 6, 4, 28)), ("Log", "use · unused", new Range(6, 6, 6, 28))],
            flow.Select(item => (item.Text, item.Detail, item.Location?.Range)));
        var served = Assert.Single(flow[1].Children!);
        Assert.Equal(("note", new Range(5, 2, 5, 6)), (served.Text, served.Location?.Range));
        Assert.Equal(
            [("provider", new Range(4, 6, 4, 28)), ("call", new Range(5, 2, 5, 6))],
            view.Highlights.Select(highlight => (highlight.Kind, highlight.Range)));
        Assert.Equal(
            [("provider", new Range(2, 11, 2, 14)), ("call", new Range(3, 2, 3, 6))],
            (await InfoViewAt(uri, 3, 3)).Highlights.Select(highlight => (highlight.Kind, highlight.Range)));
    }

    [Fact]
    public async Task Info_view_names_types_as_the_code_at_the_cursor_names_them()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-13");

        Open(uri, """
            module Points =
              export type Point = protected (x: int, y: int)

              export def Point.new: int, int -> #some(Point) | #error(string) = { x, y ->
                if x < 0 or y < 0 then #error("negative") else #some(Point(x = x, y = y))
              }

            import Points
            import Points as q

            def made = Point.new(1, 2)

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // Inside the module block that declares it, a type is named as the block's code names it.
        var inside = await InfoViewAt(uri, 4, 10);
        Assert.Equal(
            "Points.Point.new: int, int -> #some(Point) | #error(string)",
            Assert.Single(Section(inside, "function", "signature")).Text);
        Assert.Contains(
            ("x: int", "parameter"),
            Section(inside, "function", "names").Select(item => (item.Text, item.Detail)));

        // Outside it, by the name an import brings in, without its module.
        var outside = await InfoViewAt(uri, 10, 14);
        Assert.Equal(
            "def Points.Point.new: int, int -> #some(Point) | #error(string)",
            Assert.Single(Section(outside, "cursor", "expression")).Text);
        Assert.Equal("made: #some(Point) | #error(string)", Assert.Single(Section(outside, "function", "signature")).Text);
    }

    [Fact]
    public async Task Info_view_names_a_type_imported_under_an_alias_through_the_alias()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-14");

        Open(uri, """
            module Points =
              export type Point = protected (x: int, y: int)

              export def Point.origin: Point = Point(x = 0, y = 0)

            import Points as q

            def made = q.Point.origin

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 7, 5);

        Assert.Equal("made: q.Point", Assert.Single(Section(view, "function", "signature")).Text);
    }

    [Fact]
    public async Task Info_view_items_link_only_the_name_they_start_with()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-15");

        Open(uri, """
            context Log = (log: string -> ())
            def note: [Log] string -> () = { message ->
              log(message)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 2, 6);

        var signature = Assert.Single(Section(view, "function", "signature"));
        Assert.Equal(("note: [Log] string -> ()", new InfoViewName(0, 4)), (signature.Text, signature.Name));
        var parameter = Assert.Single(Section(view, "function", "names"), item => item.Detail == "parameter");
        Assert.Equal(("message: string", new InfoViewName(0, 7)), (parameter.Text, parameter.Name));
        var context = Assert.Single(Section(view, "function", "contexts"));
        Assert.Equal(("Log", new InfoViewName(0, 3)), (context.Text, context.Name));
        var flow = Assert.Single(Section(view, "function", "flow"));
        Assert.Equal(new InfoViewName(0, 3), flow.Name);
        Assert.Equal(("log", new InfoViewName(0, 3)), (Assert.Single(flow.Children!).Text, flow.Children![0].Name));
        var expression = Assert.Single(Section(view, "cursor", "expression"));
        Assert.Equal(("message: string", new InfoViewName(0, 7)), (expression.Text, expression.Name));
    }

    [Fact]
    public async Task Info_view_names_a_protected_context()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-ProtectedContext");

        Open(uri, """
            context Log = protected (log: string -> ())
            def note: [Log] string -> () = { message ->
              log(message)
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 2, 6);

        var context = Assert.Single(Section(view, "function", "contexts"));
        Assert.Equal(("Log", new InfoViewName(0, 3)), (context.Text, context.Name));
        Assert.Equal(new InfoViewName(0, 3), Assert.Single(Section(view, "function", "flow")).Name);
    }

    [Fact]
    public async Task Info_view_links_no_name_in_a_bare_type()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-16");

        Open(uri, """
            def total = [1, 2]

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var expression = Assert.Single(Section(await InfoViewAt(uri, 0, 12), "cursor", "expression"));

        Assert.Equal("list(int)", expression.Text);
        Assert.Null(expression.Name);
    }

    [Fact]
    public async Task Info_view_lays_out_a_signature_that_does_not_fit_its_width_as_the_formatter_does()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-17");

        Open(uri, """
            def parse: string, int -> #some(int) | #error(string) = { text, base -> #some(base) }

            def parsed = parse("1", 10)

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var narrow = await InfoViewAt(uri, 0, 70, width: 30);
        var signature = Assert.Single(Section(narrow, "function", "signature"));
        Assert.Equal("parse\n  : string,\n    int\n    -> | #some(int)\n       | #error(string)", signature.Text);
        Assert.Equal(new InfoViewName(0, 5), signature.Name);
        Assert.Contains("text: string", Section(narrow, "function", "names").Select(item => item.Text));
        var call = Assert.Single(Section(await InfoViewAt(uri, 2, 14, width: 30), "cursor", "expression"));
        Assert.Equal(("def parse\n  : string,\n    int\n    -> | #some(int)\n       | #error(string)", new InfoViewName(4, 5)), (call.Text, call.Name));

        var wide = await InfoViewAt(uri, 0, 70, width: 80);
        Assert.Equal("parse: string, int -> #some(int) | #error(string)", Assert.Single(Section(wide, "function", "signature")).Text);
        Assert.Equal(
            "parse: string, int -> #some(int) | #error(string)",
            Assert.Single(Section(await InfoViewAt(uri, 0, 70), "function", "signature")).Text);
    }

    // Each signature was laid out by the formatter, though nearly all fit on one line as they are, which took ten times
    // the rest of a request: a request with a width now lays out only those that don't fit (#2406). It counts them, rather
    // than timing requests, which a loaded machine makes slow and noisy (#2977).
    [Fact]
    public async Task Info_view_lays_out_only_the_signatures_that_do_not_fit()
    {
        // The server counts what it lays out in the flow of work it was started in.
        var laidOut = Nanyx.Formatter.SignatureLayout.startCounting();
        await RestartWithAsync(TestSettings);
        var uri = DocumentUri.Parse("untitled:InfoView-Width-Cost");
        var defs = string.Join("\n", Enumerable.Range(1, 400).Select(index => $"def value{index}: int -> int = {{ x -> x + {index} }}"));
        const string tooLong = "def describe: (name: string, age: int, address: string) -> string = { p -> p.name }";
        Open(uri, defs + "\n" + tooLong + "\n\ndef last: int -> int = { x ->\n  def doubled = x * 2\n  doubled\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 403, 3, 60);
        Assert.Contains("value400: int -> int", Section(view, "module", "names").Select(item => item.Text));
        // Only the few names whose signatures are longer than 60 columns: `describe`, and some the host brings in. Each of
        // the 450 names the view shows was laid out before #2406.
        Assert.InRange(laidOut.Value, 1, 9);
        laidOut.Value = 0;
        await InfoViewAt(uri, 403, 3);
        Assert.Equal(0, laidOut.Value);
    }

    [Fact]
    public async Task Info_view_links_each_type_name_to_its_definition()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-18");

        Open(uri, """
            type Point = protected (x: int, y: int)

            def Point.new: int, int -> #some(Point) | #error(string) = { x, y -> #some(Point(x = x, y = y)) }

            def moved = { p: Point -> p }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 4, 23);

        var parameter = Assert.Single(Section(view, "function", "names"), item => item.Detail == "parameter");
        Assert.Equal("p: Point", parameter.Text);
        var link = Assert.Single(parameter.Links!);
        Assert.Equal((3, 5, new Range(0, 5, 0, 10)), (link.Start, link.Length, link.Location.Range));
        var made = Assert.Single(Section(await InfoViewAt(uri, 2, 75), "function", "signature"));
        Assert.Equal(
            ["Point"],
            made.Links!.Select(found => made.Text.Substring(found.Start, found.Length)));
        Assert.Equal(made.Text.IndexOf("#some(Point)") + "#some(".Length, made.Links![0].Start);
    }
}

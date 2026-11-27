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
    public async Task Info_view_starts_at_the_innermost_scope_and_shows_the_names_in_scope_nearest_first()
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

        // With no message on the line, the expression at the cursor comes first, then the innermost def.
        Assert.Equal(["expression", "function", "module"], view.Scopes.Select(scope => scope.Kind));
        Assert.Equal("shout: string", Assert.Single(Section(view, "expression", "expression")).Text);
        Assert.DoesNotContain(view.Scopes.SelectMany(scope => scope.Sections), section => section.Id is "call");
        Assert.Equal(
            [("shout: string", "local", new Range(1, 6, 1, 11)), ("name: string", "parameter", new Range(0, 32, 0, 36))],
            Section(view, "function", "names").Select(item => (item.Text, item.Detail, item.Location?.Range)));
        Assert.True(view.Scopes[^1].Collapsed);
        var topLevel = Section(view, "module", "names");
        Assert.Equal(("greet: string -> string", "def"), (topLevel[0].Text, topLevel[0].Detail));
        Assert.DoesNotContain(topLevel, item => item.Text.StartsWith("__"));
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
    public async Task Info_view_shows_the_messages_on_the_line_first()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-4");

        Open(uri, """
            println(missing)

            """.ReplaceLineEndings("\n"));
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 0);
        Assert.Equal(("messages", "Messages"), (view.Scopes[0].Kind, view.Scopes[0].Heading));
        var message = Assert.Single(view.Scopes[0].Messages!);
        Assert.Equal(("error", "NYX0101"), (message.Severity, message.Code));
        Assert.Contains("missing", message.Headline + string.Join("\n", message.Notes));
        Assert.Equal(new Range(0, 8, 0, 15), message.Location.Range);
        Assert.Equal(["module"], (await InfoViewAt(uri, 1, 0)).Scopes.Select(scope => scope.Kind));
    }

    [Fact]
    public async Task Info_view_shows_a_type_mismatch_in_full()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Mismatch");

        Open(uri, """
            def half: () -> int = {
              2.5
            }

            """.ReplaceLineEndings("\n"));
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 1, 3);

        Assert.Equal(["messages", "expression", "function", "module"], view.Scopes.Select(scope => scope.Kind));
        var message = Assert.Single(view.Scopes[0].Messages!);
        Assert.Equal(("error", "NYX0201", "Type mismatch"), (message.Severity, message.Code, message.Headline));
        Assert.Equal(["'float' vs 'int'"], message.Notes);
        Assert.Equal(new Uri("https://nanyx.dev/docs/standard-tools/errors#nyx0201"), message.Reference);
        Assert.Equal(new Range(1, 2, 1, 5), message.Location.Range);
        // The messages and the expression don't push the def's own scopes towards being collapsed.
        Assert.False(view.Scopes[2].Collapsed);
    }

    [Fact]
    public async Task Info_view_offers_a_messages_fixes_with_the_edit_its_quick_fix_makes()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Fix");
        const string text = "def s = max(1, 2)\n";

        Open(uri, text);
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var message = Assert.Single((await InfoViewAt(uri, 0, 10)).Scopes[0].Messages!);
        var fix = Assert.Single(message.Fixes);
        Assert.Equal("Add `import nanyx/math`", fix.Title);
        Assert.Equal("import nanyx/math\n\ndef s = max(1, 2)\n", Applied(text, Assert.Single(fix.Edit.Changes![uri])));
    }

    [Fact]
    public async Task Info_view_links_a_messages_other_places()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Related");

        Open(uri, """
            def twice = 1
            def twice = 2

            """.ReplaceLineEndings("\n"));
        var published = Assert.Single((await NextPublished(uri)).Diagnostics!);
        var related = Assert.Single(published.RelatedInformation!);

        var message = Assert.Single((await InfoViewAt(uri, published.Range.Start.Line, 0)).Scopes[0].Messages!);
        var place = Assert.Single(message.Related);
        Assert.Equal((related.Message, related.Location.Range, $"line {related.Location.Range.Start.Line + 1}"), (place.Text, place.Location?.Range, place.Detail));
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

        Assert.Equal("n: int", Assert.Single(Section(view, "function", "names")).Text);
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
    public async Task Info_view_shows_the_selected_expression_with_its_type_at_the_top()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Selection");

        Open(uri, "def f = { x -> x + 1 }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 20, selection: new Range(0, 15, 0, 20));

        Assert.Equal(("expression", "expression", "x + 1", new Range(0, 15, 0, 20)), (view.Scopes[0].Kind, view.Scopes[0].Label, view.Scopes[0].Heading, view.Scopes[0].Location?.Range));
        var selected = Assert.Single(Section(view, "expression", "expression"));
        Assert.Equal(("x + 1: int", new Range(0, 15, 0, 20)), (selected.Text, selected.Location?.Range));
        // The spaces around a selection are left out.
        var spaced = await InfoViewAt(uri, 0, 21, selection: new Range(0, 14, 0, 21));
        Assert.Equal("x + 1: int", Assert.Single(Section(spaced, "expression", "expression")).Text);
    }

    [Fact]
    public async Task Info_view_shows_no_expression_for_a_selection_that_is_not_one()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Part");

        Open(uri, "def f = { x -> x + 1 }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 18, selection: new Range(0, 15, 0, 18));

        Assert.Equal(["function", "module"], view.Scopes.Select(scope => scope.Kind));
    }

    [Fact]
    public async Task Info_view_shows_the_expression_at_the_cursor_with_no_selection()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Cursor");

        Open(uri, "def f = { x -> x + 1 }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 15, selection: new Range(0, 15, 0, 15));

        Assert.Equal(("expression", "expression", "x"), (view.Scopes[0].Kind, view.Scopes[0].Label, view.Scopes[0].Heading));
        var atCursor = Assert.Single(Section(view, "expression", "expression"));
        Assert.Equal(("x: int", new Range(0, 15, 0, 16)), (atCursor.Text, atCursor.Location?.Range));
        // The parameter it names is still shown where it is declared.
        Assert.Contains(Section(view, "function", "names"), item => item.Text == "x: int");
    }

    [Fact]
    public async Task Info_view_shows_a_defs_signature_once_with_the_cursor_on_its_name()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-DefName");

        Open(uri, """
            def twice: int -> int = { n -> n * 2 }
            def f = { x -> x + 1 }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        foreach (var (line, character, signature, detail) in new[] { (0, 5, "twice: int -> int", "declared"), (1, 4, "f: int -> int", "inferred") })
        {
            var view = await InfoViewAt(uri, line, character);

            // The def's scope shows it, with its context; the expression at the cursor would only repeat it.
            Assert.Equal(["function", "module"], view.Scopes.Select(scope => scope.Kind));
            var shown = view.Scopes
                .Where(scope => scope.Kind != "module")
                .SelectMany(scope => scope.Sections)
                .SelectMany(section => section.Items)
                .Where(item => item.Text == signature);
            Assert.Equal(detail, Assert.Single(shown).Detail);
        }
    }

    [Fact]
    public async Task Info_view_shows_only_the_cursor_and_the_module_at_the_top_level()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-14");

        Open(uri, "def one = 1\none\n");
        await NextPublished(uri);

        var view = await InfoViewAt(uri, 1, 1);

        Assert.Equal([("expression", "one"), ("module", "this module")], view.Scopes.Select(scope => (scope.Kind, scope.Heading)));
        Assert.Equal("one: int", Assert.Single(Section(view, "expression", "expression")).Text);
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
                ("expression", "log", false), ("lambda", "{ x -> … } (line 5)", false), ("function", "def report", false),
                ("function", "def run", false), ("module", "this module", true)
            ],
            view.Scopes.Select(scope => (scope.Kind, scope.Heading, scope.Collapsed)));
        Assert.Equal(["expression", "lambda", null, null, null], view.Scopes.Select(scope => scope.Label));
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
    public async Task Info_view_heads_the_expression_with_its_code_on_one_line_cut_when_long()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-ExpressionHeading");

        Open(uri, """
            def emit: string -> () = { s -> () }
            def main = {
              emit("a")
              def total = [1, 2, 3]
                \list.map { n -> n * 1000 + n * 100 + n * 10 }
              total
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var atName = await InfoViewAt(uri, 2, 3);
        Assert.Equal(("expression", "emit", new Range(2, 2, 2, 6)), (atName.Scopes[0].Label, atName.Scopes[0].Heading, atName.Scopes[0].Location?.Range));
        // Selected or at the cursor, the expression is headed the same way.
        var selected = await InfoViewAt(uri, 2, 11, selection: new Range(2, 2, 2, 11));
        Assert.Equal(("expression", """emit("a")"""), (selected.Scopes[0].Label, selected.Scopes[0].Heading));
        // A long expression is written on one line and cut.
        var long_ = await InfoViewAt(uri, 4, 50, selection: new Range(3, 14, 4, 50));
        Assert.Equal("[1, 2, 3] \\list.map { n -> n * 1000 + n …", long_.Scopes[0].Heading);
        Assert.DoesNotContain(
            new[] { atName, selected, long_ }.SelectMany(view => view.Scopes),
            scope => scope.Heading is "At the cursor" or "Selection");
    }

    [Fact]
    public async Task Info_view_heads_a_for_loops_lambda_as_the_loop_is_written()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-ForHeading");

        Open(uri, """
            def emit: string -> () = { s -> () }
            def main = {
              for ["a", "b"] | c ->
                emit(c)
              for [1, 2], ["x", "y"] | n, s -> emit(s)
              for 1..3 -> emit("again")
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var inLoop = await InfoViewAt(uri, 3, 10);
        var loop = inLoop.Scopes.Single(scope => scope.Kind == "lambda");
        Assert.Equal(((string?)null, """for ["a", "b"] | c -> … (line 3)""", new Range(2, 2, 3, 11)), (loop.Label, loop.Heading, loop.Location?.Range));
        Assert.Equal(["c: string"], Section(inLoop, "lambda", "names").Select(item => item.Text));
        Assert.Equal(
            """for [1, 2], ["x", "y"] | n, s -> … (line 5)""",
            (await InfoViewAt(uri, 4, 39)).Scopes.Single(scope => scope.Kind == "lambda").Heading);
        Assert.Equal("for 1..3 -> … (line 6)", (await InfoViewAt(uri, 5, 16)).Scopes.Single(scope => scope.Kind == "lambda").Heading);
        Assert.DoesNotContain(inLoop.Scopes, scope => scope.Heading.Contains("__for_element"));
    }

    [Fact]
    public async Task Info_view_heads_a_shorthand_lambda_as_it_is_written()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-ShorthandHeading");

        Open(uri, """
            def doubled = [1, 2] \list.map { * 2 }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 0, 35);
        var lambda = view.Scopes.Single(scope => scope.Kind == "lambda");
        Assert.Equal(("lambda", "{ * 2 } (line 1)"), (lambda.Label, lambda.Heading));
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
        Assert.Equal(["expression", "function", "module"], inMemory.Scopes.Select(scope => scope.Kind));
        Assert.Equal(["sum: int", "n: int"], Section(inMemory, "function", "names").Select(item => item.Text));
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

        Assert.Equal("def main", view.Scopes.First(scope => scope.Kind == "function").Heading);
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
    public async Task Info_view_shows_an_instance_the_compiler_builds_in_the_flow_with_the_calls_it_serves()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Built");

        Open(uri, """
            context Show(a) = (show: a -> string)
            type Point = (x: int, y: int)
            def describe: [Show(a)] a -> string = { x -> show(x) }
            def Point.show: Point -> string = { p -> "p" }
            def main = { describe(Point(x = 1, y = 2)) }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var built = Assert.Single(Section(await InfoViewAt(uri, 4, 14), "function", "flow"));
        Assert.Equal(("Show(Point)", "attached Point.show · 1 call", null), (built.Text, built.Detail, built.Location));
        Assert.Equal(("describe", new Range(4, 13, 4, 21)), (Assert.Single(built.Children!).Text, built.Children![0].Location?.Range));
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

    [Fact]
    public async Task Info_view_writes_a_custom_pattern_as_it_was_declared()
    {
        var uri = DocumentUri.Parse("untitled:InfoView-Patterns");

        Open(uri, """
            pattern int -> #Even | _ = { n -> n % 2 == 0 }

            def parse: string -> float = { s ->
              pattern string -> #Num(float) | _ = { t ->
                match t \float.fromString
                  | #some(f) -> #Num(f)
                  | _ -> _
              }
              pattern string -> #Op((float, float) -> float) | _ = { t ->
                if t == "+" then #Op({ a, b -> a + b }) else _
              }
              match s
                | #Num(f) -> f
                | _ -> 0.0
            }

            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var view = await InfoViewAt(uri, 13, 11);

        // Each local pattern is written as declared, its keyword linking to the declaration, nearest first.
        Assert.Equal(
            [
                ("pattern string -> #Op((float, float) -> float) | _", "pattern", new Range(8, 10, 8, 52)),
                ("pattern string -> #Num(float) | _", "pattern", new Range(3, 10, 3, 35)),
                ("s: string", "parameter", new Range(2, 31, 2, 32))
            ],
            Section(view, "function", "names").Select(item => (item.Text, item.Detail, item.Location?.Range)));
        Assert.All(
            Section(view, "function", "names").Where(item => item.Detail == "pattern"),
            item => Assert.Equal(new InfoViewName(0, "pattern".Length), item.Name));
        // A partial pattern of one tag, whose function returns a `bool`, is written with its tag.
        var topLevel = Section(view, "module", "names");
        Assert.Contains(("pattern int -> #Even | _", "pattern"), topLevel.Select(item => (item.Text, item.Detail)));
        Assert.DoesNotContain(view.Scopes.SelectMany(scope => scope.Sections).SelectMany(section => section.Items), item => item.Text.Contains("__"));

        // Inside a pattern's body, its scope is headed by its tags, and its signature is its declaration.
        var inPattern = await InfoViewAt(uri, 5, 20);
        var scope = inPattern.Scopes.First(scope => scope.Kind == "function");
        Assert.Equal("pattern #Num", scope.Heading);
        var signature = Assert.Single(Section(inPattern, "function", "signature"));
        Assert.Equal(("pattern string -> #Num(float) | _", "declared"), (signature.Text, signature.Detail));

        // The tag a match arm uses is the pattern, written as declared.
        Assert.Equal("pattern string -> #Num(float) | _", Assert.Single(Section(await InfoViewAt(uri, 12, 7), "expression", "expression")).Text);
    }
}

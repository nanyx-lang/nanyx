module Nanyx.Formatter.Tests.Features.Imports.Imports

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Imports and module blocks are printed as written`` () =
    unchanged (normalized """import nanyx/console

import (
  nanyx/text as t
  "./local.nyx"
)""")
    unchanged (lines [ "module app"; ""; "module geometry ="; "  def origin = 0"; "  def unit = 1"; ""; "def p = geometry.origin" ])

[<Fact>]
let ``Imports stay together, however many lines they span`` () =
    unchanged (lines [ "import nanyx/console"; ""; "import ("; "  nanyx/math"; "  nanyx/text"; ")"; ""; "import nanyx/result"; ""; "def a = 1" ])

[<Fact>]
let ``Imports in a row go in one bracketed import`` () =
    Assert.Equal(lines [ "import ("; "  nanyx/math"; "  nanyx/text"; ")" ], format (lines [ "import nanyx/math"; "import nanyx/text" ]))
    Assert.Equal(
        lines [ "import ("; "  nanyx/console"; "  nanyx/math as math"; "  nanyx/text"; ")"; ""; "def a = 1" ],
        format (lines [ "import nanyx/console"; "import nanyx/math as math"; "import (nanyx/text)"; ""; "def a = 1" ]))

[<Fact>]
let ``A comment between imports goes inside the brackets`` () =
    Assert.Equal(
        lines [ "import ("; "  nanyx/math"; "  -- for padding"; "  nanyx/text"; ")" ],
        format (lines [ "import nanyx/math"; "-- for padding"; "import nanyx/text" ]))

[<Fact>]
let ``A blank line between imports keeps them apart`` () =
    unchanged (lines [ "import nanyx/math"; ""; "import nanyx/text" ])

[<Fact>]
let ``A single import stays on one line`` () =
    unchanged (lines [ "import nanyx/math"; ""; "def a = 1" ])

[<Fact>]
let ``Imports in a row in a module block go in one bracketed import too`` () =
    Assert.Equal(
        lines [ "module m ="; "  import ("; "    nanyx/math"; "    nanyx/text"; "  )"; ""; "  def a = 1" ],
        format (lines [ "module m ="; "  import nanyx/math"; "  import nanyx/text"; "  def a = 1" ]))

[<Fact>]
let ``A selection is printed after as, and after & with an alias`` () =
    unchanged (lines [ "import util as (double, triple)"; ""; "def a = double(1)" ])
    unchanged (lines [ "import util as u & (double)"; ""; "def a = double(1)" ])

[<Fact>]
let ``A selection written one name per line is printed on one`` () =
    Assert.Equal(lines [ "import util as (double, triple)" ], format (lines [ "import util as ("; "  double"; "  triple"; ")" ]))
[<Fact>]
let ``A re-export is printed as written`` () =
    unchanged (lines [ "module c"; ""; "export util (double, triple)" ])
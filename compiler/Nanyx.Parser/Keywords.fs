/// The words the language reserves: the one list the parser reads to tell a keyword from a name, and the language
/// server offers as keywords in completion. A workflow's own keywords, such as `yield` and `await`, are members of its
/// builder, not reserved words.
module Parser.Program.Keywords

let reserved: Set<string> =
    set [ "and"; "as"; "context"; "def"; "do"; "else"; "except"; "export"; "for"; "if"; "import"; "in"; "is"; "match"
          "module"; "mut"; "not"; "or"; "private"; "rec"; "return"; "then"; "try"; "type"; "typeof"; "use"; "where"
          "while"; "with" ]

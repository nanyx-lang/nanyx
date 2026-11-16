/// Characters that look like a character Nanyx reads but are not one, usually pasted from a web page or a document:
/// a minus sign for `-`, curly quotes for `"`, a no-break space for a space. The parser fails where it meets one
/// outside a string or a comment, and the compiler names it, with a fix that writes what it looks like (see
/// `NyxCompiler.LookAlikes`).
module Parser.Program.Confusables

/// A character that looks like `Ascii`, and its Unicode name. `Ascii` is what Nanyx spells that way: one or two
/// characters (`<=` for `≤`), or nothing, for a character that can't be seen.
type Confusable = { Character: char; Name: string; Ascii: string; AsciiName: string }

let private table =
    [ // Minus and dashes.
      '−', "Minus Sign", "-", "Hyphen-Minus"
      '‐', "Hyphen", "-", "Hyphen-Minus"
      '‑', "Non-Breaking Hyphen", "-", "Hyphen-Minus"
      '‒', "Figure Dash", "-", "Hyphen-Minus"
      '–', "En Dash", "-", "Hyphen-Minus"
      '—', "Em Dash", "-", "Hyphen-Minus"
      '―', "Horizontal Bar", "-", "Hyphen-Minus"
      '﹣', "Small Hyphen-Minus", "-", "Hyphen-Minus"
      '－', "Fullwidth Hyphen-Minus", "-", "Hyphen-Minus"
      // Quotes: a double-quoted string, and a single-quoted one, which is raw.
      '‘', "Left Single Quotation Mark", "'", "Apostrophe"
      '’', "Right Single Quotation Mark", "'", "Apostrophe"
      '′', "Prime", "'", "Apostrophe"
      '＇', "Fullwidth Apostrophe", "'", "Apostrophe"
      '“', "Left Double Quotation Mark", "\"", "Quotation Mark"
      '”', "Right Double Quotation Mark", "\"", "Quotation Mark"
      '″', "Double Prime", "\"", "Quotation Mark"
      '＂', "Fullwidth Quotation Mark", "\"", "Quotation Mark"
      // Spaces, and characters that can't be seen.
      ' ', "No-Break Space", " ", "Space"
      ' ', "En Quad", " ", "Space"
      ' ', "Em Quad", " ", "Space"
      ' ', "En Space", " ", "Space"
      ' ', "Em Space", " ", "Space"
      ' ', "Three-Per-Em Space", " ", "Space"
      ' ', "Four-Per-Em Space", " ", "Space"
      ' ', "Six-Per-Em Space", " ", "Space"
      ' ', "Figure Space", " ", "Space"
      ' ', "Punctuation Space", " ", "Space"
      ' ', "Thin Space", " ", "Space"
      ' ', "Hair Space", " ", "Space"
      ' ', "Narrow No-Break Space", " ", "Space"
      '　', "Ideographic Space", " ", "Space"
      '​', "Zero Width Space", "", ""
      '‌', "Zero Width Non-Joiner", "", ""
      '‍', "Zero Width Joiner", "", ""
      '⁠', "Word Joiner", "", ""
      '﻿', "Zero Width No-Break Space", "", ""
      // Operators and punctuation.
      '×', "Multiplication Sign", "*", "Asterisk"
      '÷', "Division Sign", "/", "Solidus"
      '∕', "Division Slash", "/", "Solidus"
      '≤', "Less-Than Or Equal To", "<=", "Less-Than Sign and Equals Sign"
      '≥', "Greater-Than Or Equal To", ">=", "Greater-Than Sign and Equals Sign"
      '≠', "Not Equal To", "!=", "Exclamation Mark and Equals Sign"
      '→', "Rightwards Arrow", "->", "Hyphen-Minus and Greater-Than Sign"
      '（', "Fullwidth Left Parenthesis", "(", "Left Parenthesis"
      '）', "Fullwidth Right Parenthesis", ")", "Right Parenthesis"
      '：', "Fullwidth Colon", ":", "Colon"
      '；', "Fullwidth Semicolon", ";", "Semicolon"
      '，', "Fullwidth Comma", ",", "Comma"
      ';', "Greek Question Mark", ";", "Semicolon" ]
    |> List.map (fun (character, name, ascii, asciiName) -> character, { Character = character; Name = name; Ascii = ascii; AsciiName = asciiName })
    |> Map.ofList

/// The look-alike at `offset` in `text`, if one is there.
let at (text: string) (offset: int) : Confusable option =
    if offset >= 0 && offset < text.Length then table |> Map.tryFind text.[offset] else None

/// How `confusable` is written in a message: the character in quotes with its code point and name, or, for one that
/// can't be seen, its code point and name alone.
let written (confusable: Confusable) =
    let codePoint = $"U+%04X{int confusable.Character}"
    if confusable.Ascii.Trim() = "" then $"{codePoint} ({confusable.Name})"
    else $"'{confusable.Character}' ({codePoint} {confusable.Name})"

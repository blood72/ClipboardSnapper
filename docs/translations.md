# Translation file guide

ClipboardSnapper reads editable UTF-8 JSON from `lang` beside the executable.
`en.json` and `ko.json` ship with the app. Copy a file to another language tag,
edit its strings, then restart the app and select its display name.
No rebuild, installation or hardcoded supported-language list is needed.
Files are read at startup; switching between loaded languages still applies immediately.

For a minimal additional language, create `lang/ja.json`:

```json
{
  "languageName": "日本語",
  "strings": {
    "Start": "開始",
    "Stop": "停止",
    "Example": "例: {0}"
  }
}
```

The filename supplies the language code. Use a language tag such as `ja`, `de`,
`pt-BR` or `zh-Hans`; letters/digits separated by hyphens are accepted after the
initial alphabetic tag. `languageName` is the native display name in the selector.
`strings` maps case-sensitive app keys to nonempty strings. The English file is
the complete key reference. A partial translation is valid: missing keys use the
loaded English file, then the embedded English baseline. Extra keys are ignored
by the current app, allowing a pack to include keys for another snapshot.

Keep numbered placeholders such as `{0}` and `{1}` exactly: translators may reorder
or repeat them, but must retain the same set. They insert paths, profile names or
nested error explanations. Escape literal braces as `{{` and `}}` in messages
that use numbered placeholders. Static formula-help strings containing `${...}`
are displayed literally; preserve formula tokens and option names. Keep `Raw` as
`{0}` to preserve inserted user data and diagnostic text. Use JSON escapes such as
`\n` for newlines and `\"` for quotation marks; JSON comments are not accepted.

Each file is limited to 1 MB. Invalid JSON/types, repeated string keys, conflicting
language codes, invalid tags, blank values and mismatched numbered placeholders
produce a warning and exclude that file. Other packs stay usable. File diagnostics
may include raw JSON/system parser information. Edit the file and restart to recover.

Added, edited or removed files take effect on the next launch. If the saved pack
is missing or fails validation, the app uses English with a warning. Select an
available language to save a new preference; fallback alone does not rewrite it.
User profile names, paths, clipboard contents, image files and filename formulas
are never translated or renamed. Windows-owned dialogs remain Windows-controlled.
The app translates its own UI, validation reasons and summaries while retaining
original technical failure details.

A unified [Reload settings feature](https://github.com/blood72/ClipboardSnapper/issues/19)
is tracked in the 1.0.0 backlog; the current app has no language-only reload command.

Only English and Korean are bundled and verified. The Japanese example demonstrates
how to add another language; it is not a complete supported translation. Configuration
migration between development snapshots is not required before formal 1.0.0.

# Translation file guide

ClipboardSnapper reads editable UTF-8 JSON from `lang` beside the executable.
Both artifacts ship `lang/en.json` and `lang/ko.json` as separate files. No language
pack is embedded in the executable or its runtime bundle. Keep the `lang` folder
beside the original EXE; do not edit the runtime extraction cache. The app only
reads these files and never generates, restores or overwrites them. Copy a file from the source or folder artifact to another language tag,
edit its strings, then use **Reload settings** while stopped (or restart the app)
and select its display name.
No rebuild, installation or hardcoded supported-language list is needed.
Files are read at startup and by Reload settings; switching between loaded
languages still applies immediately.

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
loaded English file, then the English file successfully read at startup. Extra keys are ignored
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
may include raw JSON/system parser information. Edit the file, then reload settings or restart to recover.

Added, edited or removed files take effect on the next settings reload or launch.
If the saved pack is missing or fails validation, the app uses English with a
warning. Korean is available only while a valid external `ko.json` is present.
Select an available language to save a new preference; fallback alone does not
rewrite it.

A valid `lang/en.json` is required at startup. Missing or invalid English produces
an actionable startup error and a failed exit before creating settings. During a
session, the successfully read English file remains in memory for diagnostics,
placeholder validation and missing-key fallback. Reloading a missing or invalid
English file warns and uses that startup snapshot; restore the file before
restarting. This snapshot is not embedded translation data. Keep English complete;
keys absent from both the loaded and startup English files display the key itself.

User profile names, paths, clipboard contents, image files and filename formulas
are never translated or renamed. Windows-owned dialogs remain Windows-controlled.
The app translates its own UI, validation reasons and summaries while retaining
original technical failure details.

**Reload settings**, under Save settings, reloads all settings and translation
packs together. Press Stop first; the command is unavailable while monitoring or
during Start/Stop. Accepted saves finish with their original options. Confirm
discarding any unsaved edits when prompted; cancel keeps them. Session history,
preview and completed totals are retained. There is no separate language-only
reload command. See [the README](../README.md#reload-settings) for the complete
configuration and pending-write policy.

Only English and Korean are bundled and verified. The Japanese example demonstrates
how to add another language; it is not a complete supported translation. Configuration
migration between development snapshots is not required before formal 1.0.0.

# Native emulator identity when using launchers

`launch-xemu.sh` is not the emulator. A saved test that can replace the wrapper but keeps the original xemu binary cannot measure a candidate build correctly.

For a wrapper bundle with a single conventional `xemu` or `xemu.exe`, baking/uploading a new saved configuration now defaults BuildFiles to both launcher and native emulator. An explicitly incomplete BuildFiles list is rejected. For a differently named binary, declare one `Inputs` entry with `Role: "emulator"`. Multiple possible native binaries require that explicit role rather than a filename guess.

The same contract is checked before saving a requested test and again during materialization. Launcher-only replacement lists are refused. The private candidate gets a hash-enabled emulator input pinned to the candidate's actual file declaration; normal materialization, preflight and input-manifest generation verify it. The retained source is not modified.

`GET /api/v1/applications/APPLICATION_ID` returns a small declared identity: native `sha256` and `executable`, plus separate `launchSha256` and `launchExecutable`. This is a declaration, not proof of completed execution. It avoids fetching the entire package manifest just to obtain the build identity.

Archived hash history verifies the launch result/validation as before, then requires a verified native input whose actual and expected hashes both match the native payload declaration. The indexed build hash is the native binary. Raw result.json and launch evidence retain their original meaning; no historical bytes are rewritten. Launcher content is additionally part of the comparison procedure, so a changed script is not hidden as an innocent native build change.

Old frozen configurations remain inspectable. A legacy launcher-only build contract cannot execute a new misleading candidate: create a corrected new revision, use the candidate bundle and explicitly request another attempt. Existing history is not automatically moved, relabeled or declared correct. Tests cannot prove what an arbitrary script chooses to execute; launchers must invoke the declared native payload and preserve the runner's arguments.

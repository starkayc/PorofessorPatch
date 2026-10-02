# PorofessorPatch

A small tool that removes the ad from Porofessor Standalone's Home window.

## What it does

Porofessor shows an ad to everyone who is not a premium user. This tool turns on the same saved flags that a premium account would set, so Porofessor stops adding the ad. Nothing is deleted, nothing is patched, and the app itself is never modified.

You only need to run it once. The flags live in Porofessor's own saved data and survive app updates.

When you open it you get a simple menu:

1. Patch Porofessor. Removes the ad. Close Porofessor first.
2. Check patch status. Shows whether the patch is active.
3. Uninstall patch. Brings the ad back.
0. Exit

If you prefer the command line, the same actions are available as:

```text
PorofessorPatch.exe --patch     Apply the patch
PorofessorPatch.exe --check     Show the current state, changes nothing
PorofessorPatch.exe --restore   Undo the patch
PorofessorPatch.exe --version   Print the tool version
```

## How it does it

Porofessor decides whether to show the ad by reading two saved values called `isPremium` and `isOWPremium`. This tool writes those values directly into Porofessor's own saved data. That is why it needs no admin rights and touches no app files. When Porofessor starts it reads the values, sees a premium user, and skips the ad.

## Building from source

You need Node.js 20 or newer.

```sh
npm install
npm run build
```

This creates `dist/PorofessorPatch.exe`. It is a single file with no installer and no other requirements.

Releases are built from source by GitHub Actions and ship with a sha256 checksum.

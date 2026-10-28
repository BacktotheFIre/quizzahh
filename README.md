# quizzahh

A Windows desktop study app for a computer science degree. It has timed multiple-choice quizzes for 33 classes, from intro CS and calculus to operating systems and networking, plus a **Code Lab** where you write real Python and run it against tests.

Built with C# and WPF on .NET Framework 4, with no NuGet packages. It compiles with the `csc.exe` that ships with Windows.

## Features

- **1,640 college-level questions** across 33 classes, grouped by year (1–4). Each class includes formula and must-know-fact drills for its subject.
- **Distractors as plausible as the right answer.** The correct choice isn't the longest one, so you can't spot it by length.
- **Realistic per-question timers.** A calculus integral gets far longer than a definition recall. Time is estimated from reading load, math and code, or set per question with `Time:`.
- **Code Lab**: 28 multi-function Python problems in four tiers:
  - **Beginner**: FizzBuzz, Caesar cipher, palindromes…
  - **Intermediate**: matrix toolkit, run-length encoding, a tic-tac-toe referee…
  - **Advanced**: a linked list, a hash map, a heap and a BST from scratch…
  - **Expert**: BFS maze solving, Dijkstra, topological sort, edit distance, a Sudoku solver, an expression parser.

  Each problem has a hint and a reference solution. Your code auto-saves, and Ctrl+Enter runs the tests.
- **Scratch paper**, per-class notes, XP and levels, mastery tracking, and an optional offline text-to-speech voice.

## Build and run

Requirements: Windows 10 or 11 (.NET Framework 4.x is built in), plus [Python 3](https://www.python.org/downloads/) for the Code Lab.

```powershell
.\build.ps1 -Install    # builds CS-Quiz.exe and launches it
```

On first launch, the app copies `data\classes` and `data\coding` into `Documents\quizzahh\`, and it reads them from there after that. Those copies are plain text files, so you can edit them or add your own.

Self-test: `.\build.ps1` builds `CS-Quiz-test.exe`. Running `CS-Quiz-test.exe --snap <folder>` checks every question file, verifies that every Code Lab solution passes its tests and that no starter code passes them, and saves screenshots.

## Writing questions

Files go in `data/classes/*.txt`. Mark the right answer with `*` and the wrong ones with `-`:

```
Year: 1

Q: What is the derivative of sin(x)?
Topic: Derivatives
* cos(x)
- -cos(x)
- sin(x)
- -sin(x)
Why: d/dx sin x = cos x.
Time: 20
```

## Writing Code Lab problems

Files go in `data/coding/*.txt`. Each test is a Python expression that must be `True`. A line starting with `> ` is setup code, and `raises(Error, fn, *args)` checks for exceptions.

```
=== Problem Title
Class: CS 4 - Programming I (Python)
Tier: 1
Minutes: 20
--- prompt
What to build. Lines indented 4 spaces show as code.
--- starter
def solve(x):
    pass
--- tests
solve(2) == 4
--- hint
...
--- solution
def solve(x):
    return x * 2
```

## Not included

- **The 3D companion character.** The app can show a 3D character model next to the quiz, but that model and its art aren't mine to share, so they aren't in this repo. Without them, the app runs without the character. To add your own, put a Collada model at `model-src\heather.dae` with its textures (`chhaa0_tex00-03.png`) and an `avatar.png` in the repo folder, then rebuild.
- **Voice models (optional).** Without them, the app falls back to the built-in Windows voice.
  - [Piper](https://github.com/rhasspy/piper/releases): unzip so `piper\piper\piper.exe` exists, and put [voices](https://huggingface.co/rhasspy/piper-voices) (`.onnx` + `.onnx.json`) in `piper\voices\`.
  - [Kokoro via sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx/releases): `kokoro\sherpa-onnx-v1.13.8-win-x64-shared-MT-Release\` and the model in `kokoro\kokoro-multi-lang-v1_0\`.

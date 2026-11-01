# quizzahh

C# 5 / .NET Framework 4 WPF study app (quizzes, Code Lab, practice exams, 3D study buddy). No Visual Studio project: `build.ps1` calls `csc.exe` directly.

## Build and test
- `.\build.ps1` builds `CS-Quiz-test.exe`. Run `.\CS-Quiz-test.exe --snap <dir>` for the self-test: it writes screenshots and `selftest.txt`, and checks every Code Lab solution passes its tests.
- `.\build.ps1 -Install` stops the running app, rebuilds `CS-Quiz.exe` and relaunches it.
- A new `.cs` file must be added to the `csc.exe` file list in `build.ps1`.
- The language is C# 5: no `$"..."` interpolation, no `?.`, no expression-bodied members.

## Files
- `Core.cs`, `Quiz.cs`, `Questions.cs`, `Exam.cs`, `Calculator.cs`, `CodeLab.cs`, `Scratch.cs`, `Heather3D.cs`
- `data\classes\*.txt` question banks and `data\coding\*.txt` Code Lab problems. The app seeds `Documents\quizzahh` from these on first run; after editing data there, copy it back to `data\` before committing.

## Git rules (public repo)
- Never commit anything `.gitignore` excludes: build output, voice models, character art/3D model, personal practice tests (`data/classes/*ALEKS*`), Code Lab saves.
- Commit with the GitHub noreply email already in the git config. Ask before pushing.

## Content style
- Wrong answers must be the same length and as plausible as the right one, so length never gives the answer away.
- College-level content, realistic per-question time limits.

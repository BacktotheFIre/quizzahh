# Builds quizzahh. -Install: stop the running app, build CS-Quiz.exe in place and relaunch it.
# Without -Install it builds CS-Quiz-test.exe (run it with --snap <dir> for the self-test + screenshots).
param([switch]$Install, [string]$Out = "")
$d = $PSScriptRoot; $fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"; $ms = "$d\model-src"
if ($Out -eq "") { $Out = if ($Install) { "$d\CS-Quiz.exe" } else { "$d\CS-Quiz-test.exe" } }
if ($Install) { Get-Process CS-Quiz -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 900 }

# Optional art: the 3D Heather model (model-src\) and avatar.png aren't in the public repo.
# Whatever is present gets embedded; without them the app still runs, just without Heather.
$res = @()
foreach ($f in @("$d\avatar.png", "$ms\heather.dae", "$ms\chhaa0_tex00.png", "$ms\chhaa0_tex01.png", "$ms\chhaa0_tex02.png", "$ms\chhaa0_tex03.png")) {
  if (Test-Path $f) { $res += "/resource:`"$f`",$(Split-Path $f -Leaf)" }
}
$ico = if (Test-Path "$d\quiz-icon2.ico") { "/win32icon:`"$d\quiz-icon2.ico`"" } else { $null }

& "$fw\csc.exe" /nologo /target:winexe /codepage:65001 /optimize $ico @res `
  /r:"$fw\WPF\PresentationFramework.dll" /r:"$fw\WPF\PresentationCore.dll" /r:"$fw\WPF\WindowsBase.dll" /r:"$fw\System.Xaml.dll" /r:System.Xml.dll `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"C:\WINDOWS\Microsoft.Net\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll" `
  /out:"$Out" "$d\Core.cs" "$d\Quiz.cs" "$d\Questions.cs" "$d\Heather3D.cs" "$d\Scratch.cs" "$d\CodeLab.cs" "$d\Calculator.cs" "$d\Exam.cs" 2>&1 | Select-String -NotMatch "CS0169|CS0414|CS0649"
"build exit: $LASTEXITCODE"
if ($Install -and $LASTEXITCODE -eq 0) {
  Start-Process "$d\CS-Quiz.exe" -ArgumentList "--startup"
  Start-Sleep -Seconds 4
  "running: " + [bool](Get-Process CS-Quiz -ErrorAction SilentlyContinue)
}

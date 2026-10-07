[CmdletBinding()]
param(
    # What to say. Several phrases may be given with -OutDirectory to render a
    # cue set without playing anything.
    [Parameter(Mandatory, Position = 0)]
    [string[]]$Text,

    # Render each phrase to <OutDirectory>\<Name>.wav instead of playing it.
    # Names come from -Name in the same order as -Text.
    [string]$OutDirectory,
    [string[]]$Name,

    [string]$Target = 'lukas@10.0.0.10',
    [int]$Port = 2449,

    # Play the freedesktop bell before speaking.
    [switch]$Bell
)

# Speaks to whoever is at the Linux test machine during hands-on keyboard checks.
# The machine has no speech engine, so Windows synthesizes the phrase and the
# remote desktop session plays it through its default sink (pw-play).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Speech

function Save-Speech {
    param([string]$Phrase, [string]$Path)

    $synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $prompt = New-Object System.Speech.Synthesis.PromptBuilder
        # Bluetooth sinks wake from idle and clip the first moment of audio.
        $prompt.AppendBreak([TimeSpan]::FromMilliseconds(700))
        $prompt.AppendText($Phrase)
        $synthesizer.SetOutputToWaveFile($Path)
        $synthesizer.Speak($prompt)
    }
    finally {
        $synthesizer.Dispose()
    }
}

if ($OutDirectory) {
    if (-not $Name -or $Name.Count -ne $Text.Count) {
        throw '-Name must give one file name per -Text phrase.'
    }

    New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
    for ($i = 0; $i -lt $Text.Count; $i++) {
        Save-Speech $Text[$i] (Join-Path $OutDirectory "$($Name[$i]).wav")
    }
    return
}

$local = Join-Path ([System.IO.Path]::GetTempPath()) "zetl-say-$PID.wav"
$remote = "/tmp/zetl-say-$PID.wav"
try {
    Save-Speech ($Text -join ' ') $local
    scp -q -P $Port $local "${Target}:$remote"
    if ($LASTEXITCODE -ne 0) { throw "scp to $Target failed." }

    $bellCommand = if ($Bell) {
        'pw-play /usr/share/sounds/freedesktop/stereo/bell.oga; '
    } else {
        ''
    }
    ssh -p $Port -o BatchMode=yes $Target `
        "export XDG_RUNTIME_DIR=/run/user/`$(id -u); ${bellCommand}pw-play $remote; rm -f $remote"
    if ($LASTEXITCODE -ne 0) { throw "Remote playback on $Target failed." }
}
finally {
    Remove-Item -LiteralPath $local -ErrorAction SilentlyContinue
}

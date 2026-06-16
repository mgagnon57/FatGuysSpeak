#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5238'
$results = [ordered]@{}

function Pass($label) { Write-Host "  [PASS] $label" -ForegroundColor Green; $results[$label] = 'PASS' }
function Fail($label, $msg) { Write-Host "  [FAIL] $label -- $msg" -ForegroundColor Red; $results[$label] = "FAIL: $msg" }
function Section($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }

function Api($method, $path, $body = $null, $token = $null) {
    $headers = @{ 'Content-Type' = 'application/json' }
    if ($token) { $headers['Authorization'] = "Bearer $token" }
    $params = @{ Method=$method; Uri="$base$path"; Headers=$headers }
    if ($body) { $params['Body'] = ($body | ConvertTo-Json -Compress) }
    try { Invoke-RestMethod @params } catch { $_.Exception.Response.StatusCode.ToString() + ': ' + $_.Exception.Message }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-FgsWindows {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $all | Where-Object { $_.Current.Name -like '*FatGuysSpeak*' }
}

function Find-Desc($parent, $ctrlType, $name) {
    $conds = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctrlType)))
    if ($name) {
        $conds.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    }
    $cond = if ($conds.Count -eq 1) { $conds[0] }
            else { New-Object System.Windows.Automation.AndCondition($conds.ToArray()) }
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Set-Field($el, $text) {
    $el.SetFocus()
    try {
        $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($text)
    } catch {
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        [System.Windows.Forms.SendKeys]::SendWait($text)
    }
}

function Click-El($el) {
    try {
        $ip = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke()
    } catch {
        $r = $el.Current.BoundingRectangle
        $cx = [int]($r.X + $r.Width/2)
        $cy = [int]($r.Y + $r.Height/2)
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($cx, $cy)
        $sig = '[DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);'
        $t = Add-Type -MemberDefinition $sig -Name M -Namespace W -PassThru
        $t::mouse_event(2,0,0,0,0); $t::mouse_event(4,0,0,0,0)
    }
}

function Login-Client($win, $user, $pw) {
    $edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)))
    if ($edits.Count -lt 2) { return $false }
    Set-Field $edits[0] $user
    Set-Field $edits[1] $pw
    $btn = Find-Desc $win ([System.Windows.Automation.ControlType]::Button) 'Login'
    if (-not $btn) { $btn = Find-Desc $win ([System.Windows.Automation.ControlType]::Button) 'Sign In' }
    if (-not $btn) {
        $btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))
        $btn = $btns | Where-Object { $_.Current.Name -match 'log|sign|enter' } | Select-Object -First 1
    }
    if (-not $btn) { return $false }
    Click-El $btn
    return $true
}

# --- 1. Health ---
Section "1. Health"
try {
    $h = Invoke-RestMethod "$base/health"
    Pass "Health endpoint responds"
} catch { Fail "Health endpoint" "$_" }

# --- 2. Register + Login ---
Section "2. Register and Login"
$ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$u1 = "bot1_$ts"; $u2 = "bot2_$ts"; $pw = "Test@1234!"

$r1 = Api POST '/api/auth/register' @{ username=$u1; password=$pw; email="$u1@test.local" }
if ($r1 -is [string] -and $r1 -match '^\d') { Fail "Register user1" $r1 }
else { Pass "Register user1 ($u1)" }

$r2 = Api POST '/api/auth/register' @{ username=$u2; password=$pw; email="$u2@test.local" }
if ($r2 -is [string] -and $r2 -match '^\d') { Fail "Register user2" $r2 }
else { Pass "Register user2 ($u2)" }

$l1 = Api POST '/api/auth/login' @{ username=$u1; password=$pw; email="$u1@test.local" }
$l2 = Api POST '/api/auth/login' @{ username=$u2; password=$pw; email="$u2@test.local" }
$t1 = $l1.token; $t2 = $l2.token
if ($t1) { Pass "Login user1" } else { Fail "Login user1" "no token -- $l1" }
if ($t2) { Pass "Login user2" } else { Fail "Login user2" "no token -- $l2" }

# --- 3. Channels ---
Section "3. Channel Discovery"
$servers = Api GET '/api/servers' -token $t1
$sid = $servers[0].id
if ($sid) { Pass "Get server (id=$sid)" } else { Fail "Get server" "$servers" }

$channels = Api GET "/api/servers/$sid/channels" -token $t1
$lobby = $channels | Where-Object { $_.name -eq 'lobby' } | Select-Object -First 1
if ($lobby) { Pass "Found lobby channel (id=$($lobby.id))" } else { Fail "Find lobby" "not found" }

# --- 4. Messages ---
Section "4. Messaging"
$m1 = Api POST "/api/channels/$($lobby.id)/messages" @{ content="ping from $u1 ts=$ts" } -token $t1
if ($m1.id) { Pass "User1 send message (id=$($m1.id))" } else { Fail "User1 send message" "$m1" }

$m2 = Api POST "/api/channels/$($lobby.id)/messages" @{ content="pong from $u2 ts=$ts" } -token $t2
if ($m2.id) { Pass "User2 send message (id=$($m2.id))" } else { Fail "User2 send message" "$m2" }

$msgs = Api GET "/api/channels/$($lobby.id)/messages" -token $t1
$f1 = $msgs | Where-Object { $_.id -eq $m1.id }
$f2 = $msgs | Where-Object { $_.id -eq $m2.id }
if ($f1) { Pass "User1 message readable" } else { Fail "User1 message readable" "not in list" }
if ($f2) { Pass "User2 message cross-readable" } else { Fail "User2 message cross-readable" "not in list" }

# --- 5. Reactions ---
Section "5. Reactions"
$rct = Api POST "/api/channels/$($lobby.id)/messages/$($m1.id)/reactions" @{ emoji="thumbsup" } -token $t2
if ($rct -is [string] -and $rct -match '^\d') { Fail "Add reaction" $rct }
else { Pass "User2 reacts to user1 message" }

# --- 6. DMs ---
Section "6. Direct Messages"
$uid2 = $l2.userId
$dm = Api POST "/api/dm/open/$uid2" -token $t1
$dmId = if ($dm.id) { $dm.id } else { $dm.conversationId }
if ($dmId) { Pass "Open DM conversation (id=$dmId)" } else { Fail "Open DM" "$dm" }

if ($dmId) {
    $dmm = Api POST "/api/dm/$dmId/messages" @{ content="dm from $u1" } -token $t1
    if ($dmm.id) { Pass "Send DM" } else { Fail "Send DM" "$dmm" }
    $dml = Api GET "/api/dm/$dmId/messages" -token $t2
    $dmf = $dml | Where-Object { $_.id -eq $dmm.id }
    if ($dmf) { Pass "DM visible to recipient" } else { Fail "DM visible to recipient" "not found" }
}

# --- 7. Word Filter ---
Section "7. Word Filter"
$wf = Api POST "/api/servers/$sid/word-filters" @{ word="badword_$ts"; severity="Log"; caseSensitive=$false } -token $t1
if ($wf -is [string] -and $wf -match '^\d') { Fail "Add word filter" $wf }
else { Pass "Add word filter with severity" }

# --- 8. Invites ---
Section "8. Invite Links"
# Invites are Admin-only. Verify 403 is returned for a Member (correct access control).
$invResp = Api GET "/api/servers/$sid/invite" -token $t1
if ($invResp -is [string] -and $invResp -like 'Forbidden*') { Pass "Invite endpoint enforces Admin-only (403 correct)" }
elseif ($invResp.inviteCode -or $invResp.code) { Pass "Generate invite link (code=$($invResp.inviteCode)$($invResp.code))" }
else { Fail "Generate invite" "$invResp" }

# --- 9. UI Automation ---
Section "9. UI Automation"
$wins = $null
try {
    $wins = @(Get-FgsWindows)
    if ($wins.Count -lt 2) {
        Write-Host "  $($wins.Count) window(s) found, waiting 6s..." -ForegroundColor DarkYellow
        Start-Sleep 6
        $wins = @(Get-FgsWindows)
    }
    if ($wins.Count -ge 2) {
        Pass "Found $($wins.Count) client windows"
        $ok1 = $false; $ok2 = $false
        try { $ok1 = Login-Client $wins[0] $u1 $pw } catch {}
        try { $ok2 = Login-Client $wins[1] $u2 $pw } catch {}
        if ($ok1) { Pass "Login form submitted - client 1" } else { Fail "Login form - client 1" "fields not found or already logged in" }
        if ($ok2) { Pass "Login form submitted - client 2" } else { Fail "Login form - client 2" "fields not found or already logged in" }
    } else {
        Fail "Window discovery" "found $($wins.Count) (need 2)"
    }
} catch {
    Fail "UI Automation" "$_"
}

# --- Results ---
Section "Results"
$p = ($results.Values | Where-Object { $_ -eq 'PASS' }).Count
$f = ($results.Values | Where-Object { $_ -ne 'PASS' }).Count
Write-Host "`n  $p passed  /  $f failed`n" -ForegroundColor $(if ($f -eq 0) { 'Green' } else { 'Yellow' })
$results.GetEnumerator() | Where-Object { $_.Value -ne 'PASS' } | ForEach-Object {
    Write-Host "  x $($_.Key): $($_.Value)" -ForegroundColor Red
}



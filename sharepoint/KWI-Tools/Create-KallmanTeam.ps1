# ============================================================
# KALLMAN TEAM CREATOR
#
# Creates one or multiple Teams.
#
# For each Team:
#   - Microsoft Team
#   - Microsoft 365 Group / email address
#   - SharePoint site
#   - Default members
#   - Additional members entered by user
#   - Standard SharePoint folders
#   - Entry in Intranet > Projects list
#
# Projects List:
#   Project name = Team name
#   Project site = SharePoint site URL
#   Hyperlink display text = "link"
# ============================================================

$ErrorActionPreference = "Stop"

Clear-Host

Write-Host ""
Write-Host "=================================================="
Write-Host "           KALLMAN TEAM CREATOR"
Write-Host "=================================================="
Write-Host ""
Write-Host "Create one Team or multiple Teams."
Write-Host ""
Write-Host "For multiple Teams, separate names with ;"
Write-Host ""
Write-Host "Example:"
Write-Host "ADIPEC 2027; Paris Air Show 2029; DSEI Japan 2029"
Write-Host ""

# ============================================================
# SETTINGS
# ============================================================

$DefaultUsers = @(
    "tk@kallman.com",
    "mikeb@kallman.com",
    "Sarak@kallman.com",
    "laurenw@kallman.com",
    "serenas@kallman.com",
    "allic@kallman.com",
    "katief@kallman.com"
)

$Folders = @(
    '$$ and Contract',
    '_Meetings',
    '_Project Management',
    'Chalet',
    'Co-Exhibitors',
    'Custom Builds',
    'Debrief',
    'Events',
    'Exhibitor Communications',
    'Exhibitor Lists',
    'Floor Plan',
    'Graphics',
    'Logos & Banner',
    'Operations',
    'Organizer Official Info',
    'Out the Door Prep',
    'Photos',
    'Pricing',
    'Registration',
    'Sales',
    'Sponsorship',
    'Survey',
    'Travel',
    'Walk-the-Show Membership',
    'Website'
)

# ------------------------------------------------------------
# INTRANET PROJECTS LIST SETTINGS
# ------------------------------------------------------------

$IntranetHostname = "kallmanworldwideinc.sharepoint.com"
$IntranetSitePath = "/sites/Intranet"

$ProjectsListDisplayName = "Projects"

$ProjectNameDisplayName = "Project name"
$ProjectSiteDisplayName = "Project site"

# ============================================================
# GET TEAM NAMES
# ============================================================

$TeamInput = Read-Host "Enter Team name(s)"

if ([string]::IsNullOrWhiteSpace($TeamInput)) {

    Write-Host ""
    Write-Host "ERROR: No Team names were entered."

    Read-Host "Press Enter to close"
    exit
}

$TeamNames = @(
    $TeamInput.Split(";") |
    ForEach-Object { $_.Trim() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique
)

if ($TeamNames.Count -eq 0) {

    Write-Host ""
    Write-Host "ERROR: No valid Team names were entered."

    Read-Host "Press Enter to close"
    exit
}

# ============================================================
# ASK FOR ADDITIONAL MEMBERS
# ============================================================

$TeamConfigs = @()

foreach ($TeamName in $TeamNames) {

    $MailAlias = $TeamName -replace '\s+', ''

    Write-Host ""
    Write-Host "--------------------------------------------------"
    Write-Host "Team:  $TeamName"
    Write-Host "Email: $MailAlias@kallman.com"
    Write-Host "--------------------------------------------------"
    Write-Host ""
    Write-Host "The 7 default members will be added automatically."
    Write-Host ""
    Write-Host "Enter any ADDITIONAL members."
    Write-Host "Separate multiple email addresses with ;"
    Write-Host "Press Enter if there are none."
    Write-Host ""

    $AdditionalInput = Read-Host "Additional members"

    $AdditionalUsers = @()

    if (-not [string]::IsNullOrWhiteSpace($AdditionalInput)) {

        $AdditionalUsers = @(
            $AdditionalInput -split '[;,]' |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
        )
    }

    $TeamConfigs += [PSCustomObject]@{
        TeamName        = $TeamName
        MailAlias       = $MailAlias
        AdditionalUsers = $AdditionalUsers
    }
}

# ============================================================
# PREVIEW
# ============================================================

Clear-Host

Write-Host ""
Write-Host "=================================================="
Write-Host "              TEAMS TO CREATE"
Write-Host "=================================================="
Write-Host ""

$Number = 1

foreach ($Config in $TeamConfigs) {

    Write-Host "$Number. $($Config.TeamName)"
    Write-Host "   Email: $($Config.MailAlias)@kallman.com"
    Write-Host "   Default members: $($DefaultUsers.Count)"

    if ($Config.AdditionalUsers.Count -gt 0) {

        Write-Host "   Additional members:"

        foreach ($User in $Config.AdditionalUsers) {
            Write-Host "      $User"
        }
    }
    else {

        Write-Host "   Additional members: None"
    }

    Write-Host ""

    $Number++
}

Write-Host "Total Teams: $($TeamConfigs.Count)"
Write-Host ""

$Confirm = Read-Host "Type YES to create these Teams"

if ($Confirm -ne "YES") {

    Write-Host ""
    Write-Host "Cancelled. Nothing was created."

    Read-Host "Press Enter to close"
    exit
}

# ============================================================
# CHECK REQUIRED MODULES
# ============================================================

Write-Host ""
Write-Host "Checking required PowerShell modules..."

if (-not (Get-Module -ListAvailable -Name MicrosoftTeams)) {

    Write-Host ""
    Write-Host "ERROR: MicrosoftTeams module is not installed."
    Write-Host ""
    Write-Host "Run:"
    Write-Host "Install-Module MicrosoftTeams -Scope CurrentUser -Force -AllowClobber"

    Read-Host "Press Enter to close"
    exit
}

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {

    Write-Host ""
    Write-Host "ERROR: Microsoft Graph is not installed."
    Write-Host ""
    Write-Host "Run:"
    Write-Host "Install-Module Microsoft.Graph -Scope CurrentUser -Force -AllowClobber"

    Read-Host "Press Enter to close"
    exit
}

# ============================================================
# CONNECT TO MICROSOFT TEAMS
# ============================================================

try {

    Write-Host ""
    Write-Host "Signing in to Microsoft Teams..."

    Connect-MicrosoftTeams | Out-Null

    Write-Host "Connected to Microsoft Teams."

}
catch {

    Write-Host ""
    Write-Host "ERROR: Could not connect to Microsoft Teams."
    Write-Host $_.Exception.Message

    Read-Host "Press Enter to close"
    exit
}

# ============================================================
# CONNECT TO MICROSOFT GRAPH
# ============================================================

try {

    Write-Host ""
    Write-Host "Signing in to Microsoft Graph..."

    Connect-MgGraph `
        -Scopes "Channel.ReadBasic.All", "Files.ReadWrite.All", "Sites.ReadWrite.All" `
        -ContextScope CurrentUser `
        -NoWelcome

    Write-Host "Connected to Microsoft Graph."

}
catch {

    Write-Host ""
    Write-Host "ERROR: Could not connect to Microsoft Graph."
    Write-Host $_.Exception.Message

    Read-Host "Press Enter to close"
    exit
}

# ============================================================
# FIND INTRANET PROJECTS LIST
# ============================================================

$ProjectsListReady = $false
$IntranetSiteId = $null
$ProjectsListId = $null
$ProjectNameFieldName = $null
$ProjectSiteFieldName = $null

Write-Host ""
Write-Host "Finding Intranet Projects list..."

try {

    # --------------------------------------------------------
    # Find Intranet site
    # --------------------------------------------------------

    $IntranetSiteUri =
    "https://graph.microsoft.com/v1.0/sites/${IntranetHostname}:$IntranetSitePath"

    $IntranetSite = Invoke-MgGraphRequest `
        -Method GET `
        -Uri $IntranetSiteUri `
        -ErrorAction Stop

    $IntranetSiteId = $IntranetSite.id

    if ([string]::IsNullOrWhiteSpace($IntranetSiteId)) {
        throw "Could not determine Intranet SharePoint Site ID."
    }

    # --------------------------------------------------------
    # Find Projects list
    # --------------------------------------------------------

    $ListsResponse = Invoke-MgGraphRequest `
        -Method GET `
        -Uri "https://graph.microsoft.com/v1.0/sites/$IntranetSiteId/lists" `
        -ErrorAction Stop

    $ProjectsList = $ListsResponse.value |
    Where-Object {
        $_.displayName -eq $ProjectsListDisplayName
    } |
    Select-Object -First 1

    if (-not $ProjectsList) {
        throw "Could not find SharePoint list named '$ProjectsListDisplayName'."
    }

    $ProjectsListId = $ProjectsList.id

    # --------------------------------------------------------
    # Find actual internal field names
    # --------------------------------------------------------

    $ColumnsResponse = Invoke-MgGraphRequest `
        -Method GET `
        -Uri "https://graph.microsoft.com/v1.0/sites/$IntranetSiteId/lists/$ProjectsListId/columns" `
        -ErrorAction Stop

    $ProjectNameColumn = $ColumnsResponse.value |
    Where-Object {

        $_.displayName -eq $ProjectNameDisplayName -or

        (
            ($_.displayName -replace '\s+', '').ToLower() -eq
            "projectname"
        )

    } |
    Select-Object -First 1

    $ProjectSiteColumn = $ColumnsResponse.value |
    Where-Object {

        $_.displayName -eq $ProjectSiteDisplayName -or

        (
            ($_.displayName -replace '\s+', '').ToLower() -eq
            "projectsite"
        )

    } |
    Select-Object -First 1

    if (-not $ProjectNameColumn) {
        throw "Could not find the 'Project name' column."
    }

    if (-not $ProjectSiteColumn) {
        throw "Could not find the 'Project site' column."
    }

    $ProjectNameFieldName = $ProjectNameColumn.name
    $ProjectSiteFieldName = $ProjectSiteColumn.name

    $ProjectsListReady = $true

    Write-Host "Projects list found."
    Write-Host "Project name field: $ProjectNameFieldName"
    Write-Host "Project site field: $ProjectSiteFieldName"

}
catch {

    Write-Host ""
    Write-Host "WARNING: Could not prepare the Projects list."
    Write-Host $_.Exception.Message
    Write-Host ""
    Write-Host "Teams will still be created."
    Write-Host "Projects list entries will be skipped."
    Write-Host ""

    $ProjectsListReady = $false
}

# ============================================================
# RESULTS STORAGE
# ============================================================

$Results = @()

$CurrentTeamNumber = 0

# ============================================================
# CREATE EACH TEAM
# ============================================================

foreach ($Config in $TeamConfigs) {

    $CurrentTeamNumber++

    $TeamName = $Config.TeamName
    $MailAlias = $Config.MailAlias

    Write-Host ""
    Write-Host ""
    Write-Host "=================================================="
    Write-Host "TEAM $CurrentTeamNumber OF $($TeamConfigs.Count)"
    Write-Host "=================================================="
    Write-Host ""
    Write-Host "Team:  $TeamName"
    Write-Host "Email: $MailAlias@kallman.com"
    Write-Host ""

    # ========================================================
    # CREATE TEAM
    # ========================================================

    try {

        Write-Host "Creating Team..."

        $team = New-Team `
            -DisplayName $TeamName `
            -MailNickName $MailAlias `
            -Visibility Private

        $TeamId = $team.GroupId

        Write-Host "Team created."
        Write-Host "Team ID: $TeamId"

    }
    catch {

        Write-Host ""
        Write-Host "FAILED TO CREATE TEAM: $TeamName"
        Write-Host $_.Exception.Message
        Write-Host ""
        Write-Host "Moving to next Team..."

        $Results += [PSCustomObject]@{
            TeamName       = $TeamName
            Email          = "$MailAlias@kallman.com"
            Status         = "FAILED - Team creation"
            UsersAdded     = 0
            UsersFailed    = 0
            FoldersCreated = 0
            FoldersFailed  = 0
            ProjectList    = "Not added"
            SharePoint     = ""
        }

        continue
    }

    # ========================================================
    # COMBINE DEFAULT + ADDITIONAL USERS
    # ========================================================

    $AllUsers = @(
        $DefaultUsers + $Config.AdditionalUsers
    ) |
    ForEach-Object { $_.Trim() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique

    # ========================================================
    # ADD TEAM USERS
    # ========================================================

    Write-Host ""
    Write-Host "Adding Team members..."
    Write-Host ""

    $UsersAdded = 0
    $UsersFailed = 0

    foreach ($user in $AllUsers) {

        try {

            Add-TeamUser `
                -GroupId $TeamId `
                -User $user `
                -Role Member `
                -ErrorAction Stop

            Write-Host "Added: $user"

            $UsersAdded++

        }
        catch {

            Write-Host "FAILED: $user"
            Write-Host $_.Exception.Message
            Write-Host ""

            $UsersFailed++

        }
    }

    # ========================================================
    # WAIT FOR SHAREPOINT
    # ========================================================

    Write-Host ""
    Write-Host "Waiting for SharePoint to finish provisioning..."
    Write-Host ""

    $FilesFolder = $null
    $PrimaryChannel = $null

    for ($Attempt = 1; $Attempt -le 30; $Attempt++) {

        try {

            $PrimaryChannel = Get-MgTeamPrimaryChannel `
                -TeamId $TeamId `
                -ErrorAction Stop

            $FilesFolder = Get-MgTeamChannelFileFolder `
                -TeamId $TeamId `
                -ChannelId $PrimaryChannel.Id `
                -Property "id,webUrl,parentReference" `
                -ErrorAction Stop

            if ($FilesFolder -and $FilesFolder.Id) {

                Write-Host "SharePoint is ready."

                break
            }

        }
        catch {

            # Normal while SharePoint provisions

        }

        Write-Host "SharePoint not ready yet. Attempt $Attempt of 30..."

        Start-Sleep -Seconds 10
    }

    # ========================================================
    # SHAREPOINT TIMEOUT
    # ========================================================

    if (-not $FilesFolder) {

        Write-Host ""
        Write-Host "WARNING:"
        Write-Host "Team was created, but SharePoint was not ready"
        Write-Host "before the timeout."

        $Results += [PSCustomObject]@{
            TeamName       = $TeamName
            Email          = "$MailAlias@kallman.com"
            Status         = "WARNING - SharePoint timeout"
            UsersAdded     = $UsersAdded
            UsersFailed    = $UsersFailed
            FoldersCreated = 0
            FoldersFailed  = $Folders.Count
            ProjectList    = "Not added"
            SharePoint     = ""
        }

        continue
    }

    # ========================================================
    # SHAREPOINT INFORMATION
    # ========================================================

    $GeneralFolderId = $FilesFolder.Id
    $DriveId = $FilesFolder.ParentReference.DriveId
    $GeneralFolderUrl = $FilesFolder.WebUrl

    # --------------------------------------------------------
    # Get actual SharePoint site homepage
    #
    # Example:
    #
    # General:
    # https://tenant.sharepoint.com/sites/ADIPEC2027/
    # Shared%20Documents/General
    #
    # Becomes:
    # https://tenant.sharepoint.com/sites/ADIPEC2027
    # --------------------------------------------------------

    $ProjectSiteUrl = $GeneralFolderUrl `
        -replace '/Shared(%20| )Documents/General/?$', ''

    Write-Host ""
    Write-Host "SharePoint site:"
    Write-Host $ProjectSiteUrl
    Write-Host ""

    # ========================================================
    # CREATE STANDARD FOLDERS
    # ========================================================

    Write-Host "Creating standard folders..."
    Write-Host ""

    $FoldersCreated = 0
    $FoldersExisting = 0
    $FoldersFailed = 0

    foreach ($FolderName in $Folders) {

        try {

            $Uri =
            "https://graph.microsoft.com/v1.0/drives/$DriveId/items/$GeneralFolderId/children"

            $Body = @{
                name                                = $FolderName
                folder                              = @{}
                "@microsoft.graph.conflictBehavior" = "fail"
            } | ConvertTo-Json -Depth 5

            Invoke-MgGraphRequest `
                -Method POST `
                -Uri $Uri `
                -Body $Body `
                -ContentType "application/json" `
                -ErrorAction Stop |
            Out-Null

            Write-Host "Created: $FolderName"

            $FoldersCreated++

        }
        catch {

            if (
                $_.Exception.Message -match "nameAlreadyExists" -or
                $_.Exception.Message -match "already exists"
            ) {

                Write-Host "Already exists: $FolderName"

                $FoldersExisting++

            }
            else {

                Write-Host "FAILED: $FolderName"
                Write-Host $_.Exception.Message
                Write-Host ""

                $FoldersFailed++

            }
        }
    }

    # ========================================================
    # ADD TO INTRANET PROJECTS LIST
    # ========================================================

    $ProjectListStatus = "Skipped"

    if ($ProjectsListReady) {

        Write-Host ""
        Write-Host "Adding project to Intranet Projects list..."

        try {

            $ProjectFields = @{}

            # ------------------------------------------------
            # PROJECT NAME
            # ------------------------------------------------

            $ProjectFields[$ProjectNameFieldName] = $TeamName

            # ------------------------------------------------
            # PROJECT SITE
            #
            # URL = new SharePoint site
            # Display text = link
            # ------------------------------------------------

            $ProjectFields[$ProjectSiteFieldName] = @{
                Url         = $ProjectSiteUrl
                Description = "link"
            }

            $ProjectBody = @{
                fields = $ProjectFields
            } | ConvertTo-Json -Depth 10

            # SharePoint hyperlink fields require this
            # preference header when writing through Graph.

            $ProjectHeaders = @{
                "Prefer" = "apiversion=2.1"
            }

            Invoke-MgGraphRequest `
                -Method POST `
                -Uri "https://graph.microsoft.com/v1.0/sites/$IntranetSiteId/lists/$ProjectsListId/items" `
                -Headers $ProjectHeaders `
                -Body $ProjectBody `
                -ContentType "application/json" `
                -ErrorAction Stop |
            Out-Null

            Write-Host "Added to Projects list."

            $ProjectListStatus = "Added"

        }
        catch {

            Write-Host ""
            Write-Host "WARNING: Could not add project to Projects list."
            Write-Host $_.Exception.Message
            Write-Host ""

            $ProjectListStatus = "FAILED"

        }

    }
    else {

        Write-Host ""
        Write-Host "Projects list entry skipped because list setup failed."

        $ProjectListStatus = "Skipped"
    }

    # ========================================================
    # DETERMINE FINAL TEAM STATUS
    # ========================================================

    if (
        ($UsersFailed -eq 0) -and
        ($FoldersFailed -eq 0) -and
        ($ProjectListStatus -eq "Added")
    ) {

        $Status = "SUCCESS"

    }
    else {

        $Status = "COMPLETED WITH WARNINGS"

    }

    # ========================================================
    # STORE RESULTS
    # ========================================================

    $Results += [PSCustomObject]@{
        TeamName       = $TeamName
        Email          = "$MailAlias@kallman.com"
        Status         = $Status
        UsersAdded     = $UsersAdded
        UsersFailed    = $UsersFailed
        FoldersCreated = $FoldersCreated
        FoldersFailed  = $FoldersFailed
        ProjectList    = $ProjectListStatus
        SharePoint     = $ProjectSiteUrl
    }

    Write-Host ""
    Write-Host "Finished: $TeamName"
}

# ============================================================
# FINAL SUMMARY
# ============================================================

Write-Host ""
Write-Host ""
Write-Host "=================================================="
Write-Host "              FINAL SUMMARY"
Write-Host "=================================================="
Write-Host ""

foreach ($Result in $Results) {

    Write-Host "--------------------------------------------------"
    Write-Host "Team:            $($Result.TeamName)"
    Write-Host "Email:           $($Result.Email)"
    Write-Host "Status:          $($Result.Status)"
    Write-Host ""
    Write-Host "Users Added:     $($Result.UsersAdded)"
    Write-Host "Users Failed:    $($Result.UsersFailed)"
    Write-Host "Folders Created: $($Result.FoldersCreated)"
    Write-Host "Folders Failed:  $($Result.FoldersFailed)"
    Write-Host "Projects List:   $($Result.ProjectList)"

    if (-not [string]::IsNullOrWhiteSpace($Result.SharePoint)) {

        Write-Host ""
        Write-Host "SharePoint:"
        Write-Host $Result.SharePoint
    }

    Write-Host ""
}

# ============================================================
# COPY ALL SHAREPOINT LINKS TO CLIPBOARD
# ============================================================

$SuccessfulLinks = @(
    $Results |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.SharePoint)
    }
)

if ($SuccessfulLinks.Count -gt 0) {

    $ClipboardText = ""

    foreach ($Result in $SuccessfulLinks) {

        $ClipboardText += "$($Result.TeamName)`r`n"
        $ClipboardText += "$($Result.SharePoint)`r`n"
        $ClipboardText += "`r`n"

    }

    try {

        Set-Clipboard -Value $ClipboardText

        Write-Host "=================================================="
        Write-Host "All SharePoint links copied to clipboard."
        Write-Host "=================================================="

    }
    catch {

        Write-Host "Could not copy SharePoint links to clipboard."

    }
}

# ============================================================
# OPEN SHAREPOINT SITE(S)
# ============================================================

if ($SuccessfulLinks.Count -eq 1) {

    try {

        Start-Process $SuccessfulLinks[0].SharePoint

        Write-Host ""
        Write-Host "SharePoint opened in your browser."

    }
    catch {

        Write-Host "Could not automatically open SharePoint."

    }

}
elseif ($SuccessfulLinks.Count -gt 1) {

    Write-Host ""

    $OpenSites =
    Read-Host "Open all $($SuccessfulLinks.Count) SharePoint sites? Type YES"

    if ($OpenSites -eq "YES") {

        foreach ($Result in $SuccessfulLinks) {

            try {

                Start-Process $Result.SharePoint

                Start-Sleep -Milliseconds 500

            }
            catch {

                Write-Host "Could not open: $($Result.TeamName)"

            }
        }
    }
}

# ============================================================
# FINAL COUNTS
# ============================================================

$SuccessCount = @(
    $Results |
    Where-Object {
        $_.Status -eq "SUCCESS"
    }
).Count

$WarningCount = @(
    $Results |
    Where-Object {
        $_.Status -eq "COMPLETED WITH WARNINGS"
    }
).Count

$FailureCount = @(
    $Results |
    Where-Object {
        $_.Status -like "FAILED*" -or
        $_.Status -like "WARNING - SharePoint*"
    }
).Count

Write-Host ""
Write-Host "=================================================="
Write-Host "COMPLETE"
Write-Host "=================================================="
Write-Host ""
Write-Host "Successful: $SuccessCount"
Write-Host "Warnings:   $WarningCount"
Write-Host "Failed:     $FailureCount"
Write-Host ""

Read-Host "Press Enter to close"
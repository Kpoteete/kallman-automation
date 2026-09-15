[CmdletBinding()]
param(
    [string]$InputFile,
    [switch]$ConfigureCredentialsOnly,
    [switch]$ConfigureMappingsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName Microsoft.VisualBasic
[Windows.Forms.Application]::EnableVisualStyles()

$script:Navy = [Drawing.ColorTranslator]::FromHtml('#082B63')
$script:Blue = [Drawing.ColorTranslator]::FromHtml('#0076A8')
$script:Red = [Drawing.ColorTranslator]::FromHtml('#A6192E')
$script:LightBlue = [Drawing.ColorTranslator]::FromHtml('#E8EEF7')
$script:LightGray = [Drawing.ColorTranslator]::FromHtml('#F6F8FB')
$script:Border = [Drawing.ColorTranslator]::FromHtml('#D9E3EA')
$script:Text = [Drawing.ColorTranslator]::FromHtml('#20262E')
$script:Muted = [Drawing.ColorTranslator]::FromHtml('#5A6573')
$script:Success = [Drawing.ColorTranslator]::FromHtml('#2E6B45')
$script:AppRoot = Join-Path $env:LOCALAPPDATA 'Kallman\MailchimpMomentusSync'
$script:CredentialPath = Join-Path $script:AppRoot 'credentials.clixml'
$script:DefaultMappingsPath = Join-Path $PSScriptRoot 'ImportMappings.json'
$script:UserMappingsPath = Join-Path $script:AppRoot 'ImportMappings.json'
$script:MappingBackupRoot = Join-Path $script:AppRoot 'mapping-backups'
$script:CatalogVersion = '2026.08.19.4'

function Show-AppError {
    param(
        [Parameter(Mandatory)][string]$Message,
        [string]$Title = 'Mailchimp to Momentus Sync'
    )

    [Windows.Forms.MessageBox]::Show(
        $Message,
        $Title,
        [Windows.Forms.MessageBoxButtons]::OK,
        [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
}

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory)][Security.SecureString]$SecureValue)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function ConvertTo-Count {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return 0
    }

    $trimmed = $Value.Trim()
    $integerValue = 0
    if ([int]::TryParse(
        $trimmed,
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$integerValue)) {
        return [Math]::Max(0, $integerValue)
    }

    $doubleValue = 0.0
    if ([double]::TryParse(
        $trimmed,
        [Globalization.NumberStyles]::Any,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$doubleValue)) {
        return [Math]::Max(0, [int][Math]::Round($doubleValue))
    }

    return 0
}

function Normalize-HeaderName {
    param([AllowNull()][string]$Value)

    $normalized = ([string]$Value).Trim().ToLowerInvariant()
    $normalized = $normalized -replace '[^a-z0-9]+', '_'
    return $normalized.Trim([char[]]'_')
}

function Get-RequiredHeaderIndex {
    param(
        [Parameter(Mandatory)][hashtable]$IndexByName,
        [Parameter(Mandatory)][string]$FriendlyName,
        [Parameter(Mandatory)][string[]]$Aliases
    )

    foreach ($alias in $Aliases) {
        $normalizedAlias = Normalize-HeaderName $alias
        if ($IndexByName.ContainsKey($normalizedAlias)) {
            return [int]$IndexByName[$normalizedAlias]
        }
    }

    throw "The selected file is missing the required '$FriendlyName' column. Accepted headers: $($Aliases -join ', ')."
}

function Get-FieldValue {
    param(
        [AllowNull()][string[]]$Fields,
        [int]$Index
    )

    if ($null -eq $Fields -or $Index -lt 0 -or $Index -ge $Fields.Count) {
        return ''
    }

    return ([string]$Fields[$Index]).Trim()
}

function Get-CampaignDetailStatus {
    param(
        [int]$Opens,
        [int]$Clicks
    )

    # A click is the strongest interaction signal. Mailchimp can occasionally
    # report a click when the tracked open count is zero, so click takes
    # precedence instead of incorrectly classifying that recipient as inactive.
    if ($Clicks -gt 0) {
        return 'CLI'
    }
    if ($Opens -gt 0) {
        return 'OPE'
    }
    return 'I'
}

function Read-RawMailchimpCsv {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The selected CSV file does not exist: $Path"
    }

    if ([IO.Path]::GetExtension($Path) -ne '.csv') {
        throw 'Select a CSV file exported from Mailchimp.'
    }

    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new(
        $Path,
        [Text.Encoding]::UTF8,
        $true)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false

    try {
        if ($parser.EndOfData) {
            throw 'The selected CSV file is empty.'
        }

        $header = $parser.ReadFields()
        if ($null -eq $header -or $header.Count -eq 0) {
            throw 'The selected CSV file does not contain a header row.'
        }

        $indexByName = @{}
        for ($i = 0; $i -lt $header.Count; $i++) {
            $normalized = Normalize-HeaderName $header[$i]
            if ([string]::IsNullOrWhiteSpace($normalized)) {
                continue
            }
            if ($indexByName.ContainsKey($normalized)) {
                throw "The selected CSV contains the header '$($header[$i])' more than once."
            }
            $indexByName[$normalized] = $i
        }

        $emailIndex = Get-RequiredHeaderIndex $indexByName 'Email' @('email', 'email_address', 'email address')
        $firstNameIndex = Get-RequiredHeaderIndex $indexByName 'First Name' @('first_name', 'first name', 'firstname')
        $lastNameIndex = Get-RequiredHeaderIndex $indexByName 'Last Name' @('last_name', 'last name', 'lastname')
        $opensIndex = Get-RequiredHeaderIndex $indexByName 'Opens' @('opens', 'open_count', 'open count')
        $clicksIndex = Get-RequiredHeaderIndex $indexByName 'Clicks' @('clicks', 'click_count', 'click count')
        $accountIndex = Get-RequiredHeaderIndex $indexByName 'Account Code' @(
            'account_code',
            'account code',
            'contact_account_code',
            'contact account code')

        $campaignRows = [Collections.Generic.List[object]]::new()
        $engagedCampaignRows = [Collections.Generic.List[object]]::new()
        $eligibleRows = [Collections.Generic.List[object]]::new()
        $rowsMissingAccount = [Collections.Generic.List[object]]::new()
        $clickedRowsMissingAccount = [Collections.Generic.List[object]]::new()
        $totalRows = 0
        $responseRows = 0
        $clickedRows = 0
        $skippedNoClick = 0
        $rowNumber = 1

        while (-not $parser.EndOfData) {
            $rowNumber++
            $fields = $parser.ReadFields()

            if ($null -eq $fields -or [string]::IsNullOrWhiteSpace(($fields -join ''))) {
                continue
            }

            $totalRows++
            $opens = ConvertTo-Count (Get-FieldValue $fields $opensIndex)
            $clicks = ConvertTo-Count (Get-FieldValue $fields $clicksIndex)
            $row = [pscustomobject]@{
                Email = Get-FieldValue $fields $emailIndex
                FirstName = Get-FieldValue $fields $firstNameIndex
                LastName = Get-FieldValue $fields $lastNameIndex
                Opens = $opens
                Clicks = $clicks
                ContactAccountCode = Get-FieldValue $fields $accountIndex
                SourceRow = $rowNumber
                CampaignDetailStatus = Get-CampaignDetailStatus -Opens $opens -Clicks $clicks
            }

            if ($opens -gt 0 -or $clicks -gt 0) {
                $responseRows++
            }

            if ($clicks -gt 0) {
                $clickedRows++
            }
            else {
                $skippedNoClick++
            }

            if ([string]::IsNullOrWhiteSpace([string]$row.ContactAccountCode)) {
                [void]$rowsMissingAccount.Add($row)
                if ($clicks -gt 0) {
                    [void]$clickedRowsMissingAccount.Add($row)
                }
                continue
            }

            [void]$campaignRows.Add($row)
            if ($opens -gt 0 -or $clicks -gt 0) {
                [void]$engagedCampaignRows.Add($row)
            }
            if ($clicks -gt 0) {
                [void]$eligibleRows.Add($row)
            }
        }

        if ($totalRows -eq 0) {
            throw 'The selected file has a header but no data rows.'
        }

        if ($campaignRows.Count -eq 0) {
            throw 'No campaign recipients with an Account Code were found in the selected file.'
        }


        $responsePercentage = [int][Math]::Round(
            (100.0 * $responseRows) / $totalRows,
            0,
            [MidpointRounding]::AwayFromZero)

        return [pscustomobject]@{
            Path = (Resolve-Path -LiteralPath $Path).Path
            Header = $header
            TotalRows = $totalRows
            ResponseRows = $responseRows
            ResponsePercentage = $responsePercentage
            ClickedRows = $clickedRows
            CampaignRows = $campaignRows
            CampaignRowCount = $campaignRows.Count
            EngagedCampaignRows = $engagedCampaignRows
            EngagedCampaignRowCount = $engagedCampaignRows.Count
            NoInteractionCount = @($campaignRows | Where-Object CampaignDetailStatus -eq 'I').Count
            OpenedCount = @($campaignRows | Where-Object CampaignDetailStatus -eq 'OPE').Count
            OpenedClickedCount = @($campaignRows | Where-Object CampaignDetailStatus -eq 'CLI').Count
            EligibleRows = $eligibleRows
            EligibleRowCount = $eligibleRows.Count
            SkippedNoClick = $skippedNoClick
            SkippedMissingAccount = $rowsMissingAccount.Count
            RowsMissingAccount = $rowsMissingAccount
            ClickedRowsMissingAccount = $clickedRowsMissingAccount
        }
    }
    finally {
        $parser.Close()
    }
}

function ConvertTo-CleanCatalogText {
    param([AllowNull()][string]$Value)

    $clean = ([string]$Value).Replace([char]0x00A0, [char]0x0020).Trim()
    return [Text.RegularExpressions.Regex]::Replace($clean, '\s+', ' ')
}

function ConvertTo-CatalogCode {
    param([AllowNull()][string]$Value)

    return (ConvertTo-CleanCatalogText $Value).ToUpperInvariant()
}

function ConvertTo-ImportMappings {
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$SourceKind
    )

    $campaignTypes = [Collections.Generic.List[object]]::new()
    foreach ($item in @($Config.CampaignTypes)) {
        if ($null -eq $item) {
            continue
        }

        [void]$campaignTypes.Add([pscustomobject]@{
            CampaignType = ConvertTo-CleanCatalogText ([string]$item.CampaignType)
            ClickType = ConvertTo-CatalogCode ([string]$item.ClickType)
            OpenType = ConvertTo-CatalogCode ([string]$item.OpenType)
        })
    }

    $rawSalespeople = [Collections.Generic.List[object]]::new()
    foreach ($item in @($Config.Salespeople)) {
        if ($null -eq $item) {
            continue
        }

        [void]$rawSalespeople.Add([pscustomobject]@{
            Salesperson = ConvertTo-CleanCatalogText ([string]$item.Salesperson)
            SalespersonAccountCode = ConvertTo-CatalogCode ([string]$item.SalespersonAccountCode)
        })
    }

    if ($campaignTypes.Count -eq 0) {
        throw 'At least one campaign/activity mapping is required.'
    }
    if ($rawSalespeople.Count -eq 0) {
        throw 'At least one salesperson mapping is required.'
    }

    foreach ($campaign in $campaignTypes) {
        if ([string]::IsNullOrWhiteSpace($campaign.CampaignType) -or
            [string]::IsNullOrWhiteSpace($campaign.ClickType) -or
            [string]::IsNullOrWhiteSpace($campaign.OpenType)) {
            throw 'Every campaign must have a campaign name, click activity code, and open activity code.'
        }
        if ($campaign.CampaignType.Length -gt 120 -or
            $campaign.ClickType.Length -gt 20 -or
            $campaign.OpenType.Length -gt 20) {
            throw "Campaign names may contain up to 120 characters and activity codes up to 20 characters."
        }
    }

    foreach ($salesperson in $rawSalespeople) {
        if ([string]::IsNullOrWhiteSpace($salesperson.Salesperson) -or
            [string]::IsNullOrWhiteSpace($salesperson.SalespersonAccountCode)) {
            throw 'Every salesperson must have a name and Momentus account code.'
        }
        if ($salesperson.Salesperson.Length -gt 120 -or
            $salesperson.SalespersonAccountCode.Length -gt 40) {
            throw 'Salesperson names may contain up to 120 characters and account codes up to 40 characters.'
        }
    }

    $duplicateCampaigns = @($campaignTypes |
        Group-Object { ([string]$_.CampaignType).ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicateCampaigns.Count -gt 0) {
        throw "Campaign '$($duplicateCampaigns[0].Group[0].CampaignType)' appears more than once."
    }

    $duplicatePairs = @($rawSalespeople |
        Group-Object { (([string]$_.Salesperson) + '|' + ([string]$_.SalespersonAccountCode)).ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicatePairs.Count -gt 0) {
        $duplicate = $duplicatePairs[0].Group[0]
        throw "Salesperson mapping '$($duplicate.Salesperson) / $($duplicate.SalespersonAccountCode)' appears more than once."
    }

    $duplicateCodes = @($rawSalespeople |
        Group-Object { ([string]$_.SalespersonAccountCode).ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicateCodes.Count -gt 0) {
        throw "Salesperson account code '$($duplicateCodes[0].Group[0].SalespersonAccountCode)' is assigned more than once."
    }

    $duplicatedNames = @($rawSalespeople |
        Group-Object { ([string]$_.Salesperson).ToUpperInvariant() } |
        Where-Object Count -gt 1 |
        ForEach-Object { $_.Name })

    $salespeople = @($rawSalespeople | ForEach-Object {
        $upperName = ([string]$_.Salesperson).ToUpperInvariant()
        $displayName = if ($duplicatedNames -contains $upperName) {
            '{0} ({1})' -f $_.Salesperson, $_.SalespersonAccountCode
        }
        else {
            [string]$_.Salesperson
        }

        [pscustomobject]@{
            Salesperson = [string]$_.Salesperson
            SalespersonAccountCode = [string]$_.SalespersonAccountCode
            DisplayName = $displayName
        }
    } | Sort-Object DisplayName)

    $campaignTypes = @($campaignTypes | Sort-Object CampaignType)

    $duplicateLabels = @($salespeople |
        Group-Object { ([string]$_.DisplayName).ToUpperInvariant() } |
        Where-Object Count -gt 1)
    if ($duplicateLabels.Count -gt 0) {
        throw "The salesperson dropdown would contain duplicate label '$($duplicateLabels[0].Name)'."
    }

    $version = ConvertTo-CleanCatalogText ([string]$Config.Version)
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = $script:CatalogVersion
    }

    return [pscustomobject]@{
        Version = $version
        Path = $SourcePath
        SourceKind = $SourceKind
        Salespeople = $salespeople
        CampaignTypes = $campaignTypes
    }
}

function Read-ImportMappingsFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$SourceKind
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The dropdown catalog does not exist: $Path"
    }

    try {
        $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "The dropdown catalog could not be read: $Path`n$($_.Exception.Message)"
    }

    return ConvertTo-ImportMappings -Config $config -SourcePath $Path -SourceKind $SourceKind
}

function Get-DefaultImportMappings {
    if (-not (Test-Path -LiteralPath $script:DefaultMappingsPath -PathType Leaf)) {
        throw 'The built-in dropdown catalog is missing. Reinstall the complete application package.'
    }

    return Read-ImportMappingsFile -Path $script:DefaultMappingsPath -SourceKind 'Built-in defaults'
}

function Get-ImportMappings {
    if (Test-Path -LiteralPath $script:UserMappingsPath -PathType Leaf) {
        $userMappings = Read-ImportMappingsFile -Path $script:UserMappingsPath -SourceKind 'User-managed'

        # v2.4 updates the official campaign/activity catalog. If a saved user
        # catalog is from an older release, keep its salesperson list but replace
        # the campaign mappings with the new built-in campaign mappings automatically.
        if ([string]$userMappings.Version -ne $script:CatalogVersion) {
            $defaults = Get-DefaultImportMappings
            $migrated = [pscustomobject]@{
                Version = $script:CatalogVersion
                Path = $script:UserMappingsPath
                SourceKind = 'User-managed (campaigns updated to v2.4)'
                Salespeople = $userMappings.Salespeople
                CampaignTypes = $defaults.CampaignTypes
            }
            Write-MappingConfigFile -Mappings $migrated -Path $script:UserMappingsPath
            return Read-ImportMappingsFile -Path $script:UserMappingsPath -SourceKind 'User-managed'
        }

        return $userMappings
    }

    return Get-DefaultImportMappings
}

function ConvertTo-MappingConfigObject {
    param([Parameter(Mandatory)]$Mappings)

    return [ordered]@{
        Version = $script:CatalogVersion
        UpdatedAt = (Get-Date).ToUniversalTime().ToString('o')
        CampaignTypes = @($Mappings.CampaignTypes | ForEach-Object {
            [ordered]@{
                CampaignType = [string]$_.CampaignType
                ClickType = [string]$_.ClickType
                OpenType = [string]$_.OpenType
            }
        })
        Salespeople = @($Mappings.Salespeople | ForEach-Object {
            [ordered]@{
                Salesperson = [string]$_.Salesperson
                SalespersonAccountCode = [string]$_.SalespersonAccountCode
            }
        })
    }
}

function Write-MappingConfigFile {
    param(
        [Parameter(Mandatory)]$Mappings,
        [Parameter(Mandatory)][string]$Path
    )

    $config = ConvertTo-MappingConfigObject -Mappings $Mappings
    $json = $config | ConvertTo-Json -Depth 8
    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($true))
}

function Save-ImportMappings {
    param([Parameter(Mandatory)]$Mappings)

    New-Item -ItemType Directory -Path $script:AppRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $script:MappingBackupRoot -Force | Out-Null

    if (Test-Path -LiteralPath $script:UserMappingsPath -PathType Leaf) {
        $backupName = 'ImportMappings_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff') + '.json'
        Copy-Item -LiteralPath $script:UserMappingsPath -Destination (Join-Path $script:MappingBackupRoot $backupName) -Force
    }

    $tempPath = $script:UserMappingsPath + '.tmp'
    try {
        Write-MappingConfigFile -Mappings $Mappings -Path $tempPath
        [void](Read-ImportMappingsFile -Path $tempPath -SourceKind 'Validation')
        if (Test-Path -LiteralPath $script:UserMappingsPath -PathType Leaf) {
            Remove-Item -LiteralPath $script:UserMappingsPath -Force
        }
        Move-Item -LiteralPath $tempPath -Destination $script:UserMappingsPath
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }

    return Get-ImportMappings
}

function New-CatalogGrid {
    $grid = [Windows.Forms.DataGridView]::new()
    $grid.Dock = [Windows.Forms.DockStyle]::Fill
    $grid.BackgroundColor = [Drawing.Color]::White
    $grid.BorderStyle = [Windows.Forms.BorderStyle]::FixedSingle
    $grid.AllowUserToAddRows = $true
    $grid.AllowUserToDeleteRows = $true
    $grid.AllowUserToResizeRows = $false
    $grid.RowHeadersVisible = $false
    $grid.SelectionMode = [Windows.Forms.DataGridViewSelectionMode]::FullRowSelect
    $grid.MultiSelect = $true
    $grid.AutoSizeColumnsMode = [Windows.Forms.DataGridViewAutoSizeColumnsMode]::Fill
    $grid.EditMode = [Windows.Forms.DataGridViewEditMode]::EditOnEnter
    $grid.DefaultCellStyle.SelectionBackColor = $script:LightBlue
    $grid.DefaultCellStyle.SelectionForeColor = $script:Text
    $grid.ColumnHeadersDefaultCellStyle.BackColor = $script:Navy
    $grid.ColumnHeadersDefaultCellStyle.ForeColor = [Drawing.Color]::White
    $grid.ColumnHeadersDefaultCellStyle.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $grid.EnableHeadersVisualStyles = $false
    return $grid
}

function Show-MappingEditor {
    param([Parameter(Mandatory)]$Mappings)

    $script:mappingEditorResult = $null

    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Manage Campaign and Salesperson Dropdowns'
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterParent
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::Sizable
    $form.MinimumSize = [Drawing.Size]::new(850, 590)
    $form.ClientSize = [Drawing.Size]::new(1010, 690)
    $form.BackColor = $script:LightGray
    $form.Font = [Drawing.Font]::new('Segoe UI', 9)

    $header = [Windows.Forms.Panel]::new()
    $header.Dock = [Windows.Forms.DockStyle]::Top
    $header.Height = 88
    $header.BackColor = $script:Navy
    $form.Controls.Add($header)

    $title = [Windows.Forms.Label]::new()
    $title.Text = 'MANAGE DROPDOWN LISTS'
    $title.ForeColor = [Drawing.Color]::White
    $title.Font = [Drawing.Font]::new('Segoe UI', 16, [Drawing.FontStyle]::Bold)
    $title.AutoSize = $true
    $title.Location = [Drawing.Point]::new(23, 15)
    $header.Controls.Add($title)

    $subtitle = [Windows.Forms.Label]::new()
    $subtitle.Text = 'Add, edit, or remove campaign activity mappings and salesperson account codes.'
    $subtitle.ForeColor = [Drawing.Color]::White
    $subtitle.AutoSize = $true
    $subtitle.Location = [Drawing.Point]::new(25, 51)
    $header.Controls.Add($subtitle)

    $accent = [Windows.Forms.Panel]::new()
    $accent.Dock = [Windows.Forms.DockStyle]::Bottom
    $accent.Height = 4
    $accent.BackColor = $script:Red
    $header.Controls.Add($accent)

    $tabs = [Windows.Forms.TabControl]::new()
    $tabs.Location = [Drawing.Point]::new(20, 108)
    $tabs.Anchor = [Windows.Forms.AnchorStyles]::Top -bor [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left -bor [Windows.Forms.AnchorStyles]::Right
    $tabs.Size = [Drawing.Size]::new(970, 485)
    $form.Controls.Add($tabs)

    $campaignTab = [Windows.Forms.TabPage]::new('Campaigns and Activity Types')
    $campaignTab.BackColor = [Drawing.Color]::White
    $campaignTab.Padding = [Windows.Forms.Padding]::new(14)
    $tabs.TabPages.Add($campaignTab)

    $campaignLayout = [Windows.Forms.TableLayoutPanel]::new()
    $campaignLayout.Dock = [Windows.Forms.DockStyle]::Fill
    $campaignLayout.RowCount = 3
    $campaignLayout.ColumnCount = 1
    [void]$campaignLayout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::Absolute, 50))
    [void]$campaignLayout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::Percent, 100))
    [void]$campaignLayout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::Absolute, 42))
    $campaignTab.Controls.Add($campaignLayout)

    $campaignHelp = [Windows.Forms.Label]::new()
    $campaignHelp.Text = 'Each campaign supplies one click activity code and one open activity code. Use the blank row at the bottom to add another campaign.'
    $campaignHelp.Dock = [Windows.Forms.DockStyle]::Fill
    $campaignHelp.TextAlign = [Drawing.ContentAlignment]::MiddleLeft
    $campaignHelp.ForeColor = $script:Text
    $campaignLayout.Controls.Add($campaignHelp, 0, 0)

    $campaignGrid = New-CatalogGrid
    [void]$campaignGrid.Columns.Add('CampaignType', 'Campaign Type')
    [void]$campaignGrid.Columns.Add('ClickType', 'Click Activity Code')
    [void]$campaignGrid.Columns.Add('OpenType', 'Open Activity Code')
    $campaignGrid.Columns[0].FillWeight = 58
    $campaignGrid.Columns[1].FillWeight = 21
    $campaignGrid.Columns[2].FillWeight = 21
    $campaignLayout.Controls.Add($campaignGrid, 0, 1)

    $campaignActions = [Windows.Forms.FlowLayoutPanel]::new()
    $campaignActions.Dock = [Windows.Forms.DockStyle]::Fill
    $campaignActions.FlowDirection = [Windows.Forms.FlowDirection]::LeftToRight
    $campaignActions.WrapContents = $false
    $campaignActions.Padding = [Windows.Forms.Padding]::new(0, 6, 0, 0)
    $campaignLayout.Controls.Add($campaignActions, 0, 2)

    $deleteCampaignButton = [Windows.Forms.Button]::new()
    $deleteCampaignButton.Text = 'Delete Selected Campaign'
    $deleteCampaignButton.AutoSize = $true
    $campaignActions.Controls.Add($deleteCampaignButton)

    $salesTab = [Windows.Forms.TabPage]::new('Salespeople')
    $salesTab.BackColor = [Drawing.Color]::White
    $salesTab.Padding = [Windows.Forms.Padding]::new(14)
    $tabs.TabPages.Add($salesTab)

    $salesLayout = [Windows.Forms.TableLayoutPanel]::new()
    $salesLayout.Dock = [Windows.Forms.DockStyle]::Fill
    $salesLayout.RowCount = 3
    $salesLayout.ColumnCount = 1
    [void]$salesLayout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::Absolute, 50))
    [void]$salesLayout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::Percent, 100))
    [void]$salesLayout.RowStyles.Add([Windows.Forms.RowStyle]::new([Windows.Forms.SizeType]::Absolute, 42))
    $salesTab.Controls.Add($salesLayout)

    $salesHelp = [Windows.Forms.Label]::new()
    $salesHelp.Text = 'Each salesperson requires a Momentus salesperson account code. Duplicate names are allowed only when the account codes are different.'
    $salesHelp.Dock = [Windows.Forms.DockStyle]::Fill
    $salesHelp.TextAlign = [Drawing.ContentAlignment]::MiddleLeft
    $salesHelp.ForeColor = $script:Text
    $salesLayout.Controls.Add($salesHelp, 0, 0)

    $salesGrid = New-CatalogGrid
    [void]$salesGrid.Columns.Add('Salesperson', 'Salesperson')
    [void]$salesGrid.Columns.Add('SalespersonAccountCode', 'Momentus Account Code')
    $salesGrid.Columns[0].FillWeight = 65
    $salesGrid.Columns[1].FillWeight = 35
    $salesLayout.Controls.Add($salesGrid, 0, 1)

    $salesActions = [Windows.Forms.FlowLayoutPanel]::new()
    $salesActions.Dock = [Windows.Forms.DockStyle]::Fill
    $salesActions.FlowDirection = [Windows.Forms.FlowDirection]::LeftToRight
    $salesActions.WrapContents = $false
    $salesActions.Padding = [Windows.Forms.Padding]::new(0, 6, 0, 0)
    $salesLayout.Controls.Add($salesActions, 0, 2)

    $deleteSalesButton = [Windows.Forms.Button]::new()
    $deleteSalesButton.Text = 'Delete Selected Salesperson'
    $deleteSalesButton.AutoSize = $true
    $salesActions.Controls.Add($deleteSalesButton)

    $statusLabel = [Windows.Forms.Label]::new()
    $statusLabel.Text = 'Changes are saved for this Windows user and apply immediately.'
    $statusLabel.ForeColor = $script:Muted
    $statusLabel.AutoSize = $true
    $statusLabel.Location = [Drawing.Point]::new(22, 608)
    $statusLabel.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $form.Controls.Add($statusLabel)

    $restoreButton = [Windows.Forms.Button]::new()
    $restoreButton.Text = 'Restore Built-In Defaults'
    $restoreButton.Size = [Drawing.Size]::new(155, 36)
    $restoreButton.Location = [Drawing.Point]::new(20, 638)
    $restoreButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $form.Controls.Add($restoreButton)

    $importButton = [Windows.Forms.Button]::new()
    $importButton.Text = 'Import JSON...'
    $importButton.Size = [Drawing.Size]::new(105, 36)
    $importButton.Location = [Drawing.Point]::new(185, 638)
    $importButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $form.Controls.Add($importButton)

    $exportButton = [Windows.Forms.Button]::new()
    $exportButton.Text = 'Export JSON...'
    $exportButton.Size = [Drawing.Size]::new(105, 36)
    $exportButton.Location = [Drawing.Point]::new(300, 638)
    $exportButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $form.Controls.Add($exportButton)

    $cancelButton = [Windows.Forms.Button]::new()
    $cancelButton.Text = 'Cancel'
    $cancelButton.Size = [Drawing.Size]::new(100, 36)
    $cancelButton.Location = [Drawing.Point]::new(778, 638)
    $cancelButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Right
    $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelButton)

    $saveButton = [Windows.Forms.Button]::new()
    $saveButton.Text = 'Save Lists'
    $saveButton.Size = [Drawing.Size]::new(105, 36)
    $saveButton.Location = [Drawing.Point]::new(885, 638)
    $saveButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Right
    $saveButton.BackColor = $script:Navy
    $saveButton.ForeColor = [Drawing.Color]::White
    $saveButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $saveButton.FlatAppearance.BorderSize = 0
    $form.Controls.Add($saveButton)

    $form.AcceptButton = $saveButton
    $form.CancelButton = $cancelButton

    $populateGrids = {
        param($MappingSet)

        $campaignGrid.Rows.Clear()
        foreach ($campaign in $MappingSet.CampaignTypes) {
            [void]$campaignGrid.Rows.Add(
                [string]$campaign.CampaignType,
                [string]$campaign.ClickType,
                [string]$campaign.OpenType)
        }

        $salesGrid.Rows.Clear()
        foreach ($salesperson in $MappingSet.Salespeople) {
            [void]$salesGrid.Rows.Add(
                [string]$salesperson.Salesperson,
                [string]$salesperson.SalespersonAccountCode)
        }

        $statusLabel.Text = ('{0}: {1} campaign(s), {2} salesperson mapping(s).' -f $MappingSet.SourceKind, $MappingSet.CampaignTypes.Count, $MappingSet.Salespeople.Count)
    }

    $readGrids = {
        [void]$campaignGrid.EndEdit()
        [void]$salesGrid.EndEdit()

        $campaignItems = [Collections.Generic.List[object]]::new()
        foreach ($row in $campaignGrid.Rows) {
            if ($row.IsNewRow) {
                continue
            }

            $name = ConvertTo-CleanCatalogText ([string]$row.Cells[0].Value)
            $click = ConvertTo-CatalogCode ([string]$row.Cells[1].Value)
            $open = ConvertTo-CatalogCode ([string]$row.Cells[2].Value)
            if ([string]::IsNullOrWhiteSpace($name) -and
                [string]::IsNullOrWhiteSpace($click) -and
                [string]::IsNullOrWhiteSpace($open)) {
                continue
            }

            [void]$campaignItems.Add([pscustomobject]@{
                CampaignType = $name
                ClickType = $click
                OpenType = $open
            })
        }

        $salesItems = [Collections.Generic.List[object]]::new()
        foreach ($row in $salesGrid.Rows) {
            if ($row.IsNewRow) {
                continue
            }

            $name = ConvertTo-CleanCatalogText ([string]$row.Cells[0].Value)
            $code = ConvertTo-CatalogCode ([string]$row.Cells[1].Value)
            if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($code)) {
                continue
            }

            [void]$salesItems.Add([pscustomobject]@{
                Salesperson = $name
                SalespersonAccountCode = $code
            })
        }

        $config = [pscustomobject]@{
            Version = $script:CatalogVersion
            CampaignTypes = $campaignItems
            Salespeople = $salesItems
        }
        return ConvertTo-ImportMappings -Config $config -SourcePath $script:UserMappingsPath -SourceKind 'User-managed'
    }

    $deleteSelectedRows = {
        param([Windows.Forms.DataGridView]$Grid)

        $indices = @($Grid.SelectedRows |
            Where-Object { -not $_.IsNewRow } |
            ForEach-Object { $_.Index } |
            Sort-Object -Descending)
        foreach ($index in $indices) {
            $Grid.Rows.RemoveAt([int]$index)
        }
    }

    $deleteCampaignButton.Add_Click({ & $deleteSelectedRows $campaignGrid })
    $deleteSalesButton.Add_Click({ & $deleteSelectedRows $salesGrid })

    $restoreButton.Add_Click({
        $answer = [Windows.Forms.MessageBox]::Show(
            $form,
            'Replace the working lists with the built-in defaults? Click Save Lists afterward to make the change permanent.',
            'Restore built-in defaults',
            [Windows.Forms.MessageBoxButtons]::YesNo,
            [Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -eq [Windows.Forms.DialogResult]::Yes) {
            try {
                $defaults = Get-DefaultImportMappings
                & $populateGrids $defaults
                $statusLabel.Text = 'Built-in defaults loaded. Click Save Lists to apply them.'
            }
            catch {
                Show-AppError $_.Exception.Message 'Could not load defaults'
            }
        }
    })

    $importButton.Add_Click({
        $dialog = [Windows.Forms.OpenFileDialog]::new()
        $dialog.Title = 'Import a dropdown catalog'
        $dialog.Filter = 'JSON files (*.json)|*.json'
        $dialog.Multiselect = $false
        try {
            if ($dialog.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) {
                $imported = Read-ImportMappingsFile -Path $dialog.FileName -SourceKind 'Imported preview'
                & $populateGrids $imported
                $statusLabel.Text = 'Imported file loaded. Click Save Lists to apply it.'
            }
        }
        catch {
            Show-AppError $_.Exception.Message 'Could not import dropdown catalog'
        }
        finally {
            $dialog.Dispose()
        }
    })

    $exportButton.Add_Click({
        try {
            $working = & $readGrids
            $dialog = [Windows.Forms.SaveFileDialog]::new()
            $dialog.Title = 'Export dropdown catalog'
            $dialog.Filter = 'JSON files (*.json)|*.json'
            $dialog.FileName = 'Kallman-Mailchimp-Momentus-Dropdowns.json'
            try {
                if ($dialog.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) {
                    Write-MappingConfigFile -Mappings $working -Path $dialog.FileName
                    [Windows.Forms.MessageBox]::Show(
                        $form,
                        'The dropdown catalog was exported successfully.',
                        'Export complete',
                        [Windows.Forms.MessageBoxButtons]::OK,
                        [Windows.Forms.MessageBoxIcon]::Information) | Out-Null
                }
            }
            finally {
                $dialog.Dispose()
            }
        }
        catch {
            Show-AppError $_.Exception.Message 'Could not export dropdown catalog'
        }
    })

    $saveButton.Add_Click({
        try {
            $working = & $readGrids
            $saved = Save-ImportMappings -Mappings $working
            $script:mappingEditorResult = $saved
            $form.DialogResult = [Windows.Forms.DialogResult]::OK
            $form.Close()
        }
        catch {
            Show-AppError $_.Exception.Message 'Dropdown lists were not saved'
        }
    })

    & $populateGrids $Mappings
    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:mappingEditorResult
}

function Get-SavedCredential {
    if (-not (Test-Path -LiteralPath $script:CredentialPath -PathType Leaf)) {
        return $null
    }

    try {
        $credential = Import-Clixml -LiteralPath $script:CredentialPath
        if ([string]::IsNullOrWhiteSpace([string]$credential.ApiUser) -or
            $null -eq $credential.Secret -or
            $null -eq $credential.Key) {
            throw 'The saved credential file is incomplete.'
        }
        return $credential
    }
    catch {
        throw "The saved Momentus credentials could not be read for this Windows user. Open Settings and replace them. $($_.Exception.Message)"
    }
}

function Show-CredentialDialog {
    param([switch]$RequireCredentials)

    $existing = $null
    try {
        $existing = Get-SavedCredential
    }
    catch {
        if (-not $RequireCredentials) {
            Show-AppError $_.Exception.Message 'Credential error'
        }
    }

    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Momentus API Credentials'
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ClientSize = [Drawing.Size]::new(590, 430)
    $form.BackColor = [Drawing.Color]::White
    $form.Font = [Drawing.Font]::new('Segoe UI', 9)

    $header = [Windows.Forms.Panel]::new()
    $header.Dock = [Windows.Forms.DockStyle]::Top
    $header.Height = 82
    $header.BackColor = $script:Navy
    $form.Controls.Add($header)

    $title = [Windows.Forms.Label]::new()
    $title.Text = 'MOMENTUS API CREDENTIALS'
    $title.ForeColor = [Drawing.Color]::White
    $title.Font = [Drawing.Font]::new('Segoe UI', 15, [Drawing.FontStyle]::Bold)
    $title.AutoSize = $true
    $title.Location = [Drawing.Point]::new(22, 17)
    $header.Controls.Add($title)

    $subtitle = [Windows.Forms.Label]::new()
    $subtitle.Text = 'Saved with Windows encryption for this Windows user on this computer.'
    $subtitle.ForeColor = [Drawing.Color]::White
    $subtitle.Font = [Drawing.Font]::new('Segoe UI', 9)
    $subtitle.AutoSize = $true
    $subtitle.Location = [Drawing.Point]::new(24, 50)
    $header.Controls.Add($subtitle)

    $accent = [Windows.Forms.Panel]::new()
    $accent.Dock = [Windows.Forms.DockStyle]::Top
    $accent.Height = 4
    $accent.BackColor = $script:Red
    $form.Controls.Add($accent)
    $accent.BringToFront()

    $apiUserLabel = [Windows.Forms.Label]::new()
    $apiUserLabel.Text = 'API User ID'
    $apiUserLabel.AutoSize = $true
    $apiUserLabel.Location = [Drawing.Point]::new(28, 116)
    $form.Controls.Add($apiUserLabel)

    $apiUserText = [Windows.Forms.TextBox]::new()
    $apiUserText.Location = [Drawing.Point]::new(28, 138)
    $apiUserText.Size = [Drawing.Size]::new(530, 25)
    $apiUserText.Text = if ($null -ne $existing) { [string]$existing.ApiUser } else { '' }
    $form.Controls.Add($apiUserText)

    $secretLabel = [Windows.Forms.Label]::new()
    $secretLabel.Text = 'Secret'
    $secretLabel.AutoSize = $true
    $secretLabel.Location = [Drawing.Point]::new(28, 181)
    $form.Controls.Add($secretLabel)

    $secretText = [Windows.Forms.TextBox]::new()
    $secretText.Location = [Drawing.Point]::new(28, 203)
    $secretText.Size = [Drawing.Size]::new(530, 25)
    $secretText.UseSystemPasswordChar = $true
    $form.Controls.Add($secretText)

    $keyLabel = [Windows.Forms.Label]::new()
    $keyLabel.Text = 'Key'
    $keyLabel.AutoSize = $true
    $keyLabel.Location = [Drawing.Point]::new(28, 246)
    $form.Controls.Add($keyLabel)

    $keyText = [Windows.Forms.TextBox]::new()
    $keyText.Location = [Drawing.Point]::new(28, 268)
    $keyText.Size = [Drawing.Size]::new(530, 25)
    $keyText.UseSystemPasswordChar = $true
    $form.Controls.Add($keyText)

    $notice = [Windows.Forms.Label]::new()
    $notice.Text = 'Credentials are not stored in the application folder or in the dropdown catalog.'
    $notice.ForeColor = $script:Muted
    $notice.AutoSize = $true
    $notice.Location = [Drawing.Point]::new(28, 314)
    $form.Controls.Add($notice)

    $cancelButton = [Windows.Forms.Button]::new()
    $cancelButton.Text = 'Cancel'
    $cancelButton.Location = [Drawing.Point]::new(358, 365)
    $cancelButton.Size = [Drawing.Size]::new(95, 34)
    $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelButton)

    $saveButton = [Windows.Forms.Button]::new()
    $saveButton.Text = 'Save Credentials'
    $saveButton.Location = [Drawing.Point]::new(463, 365)
    $saveButton.Size = [Drawing.Size]::new(95, 34)
    $saveButton.BackColor = $script:Navy
    $saveButton.ForeColor = [Drawing.Color]::White
    $saveButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $saveButton.FlatAppearance.BorderSize = 0
    $form.Controls.Add($saveButton)

    $form.AcceptButton = $saveButton
    $form.CancelButton = $cancelButton
    $script:credentialSaved = $false

    $saveButton.Add_Click({
        $apiUser = $apiUserText.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($apiUser)) {
            [Windows.Forms.MessageBox]::Show(
                $form,
                'API User ID cannot be blank.',
                'Credentials required',
                [Windows.Forms.MessageBoxButtons]::OK,
                [Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }

        $secret = if (-not [string]::IsNullOrWhiteSpace($secretText.Text)) {
            ConvertTo-SecureString -String $secretText.Text -AsPlainText -Force
        }
        elseif ($null -ne $existing) {
            $existing.Secret
        }
        else {
            $null
        }

        $key = if (-not [string]::IsNullOrWhiteSpace($keyText.Text)) {
            ConvertTo-SecureString -String $keyText.Text -AsPlainText -Force
        }
        elseif ($null -ne $existing) {
            $existing.Key
        }
        else {
            $null
        }

        if ($null -eq $secret -or $null -eq $key) {
            [Windows.Forms.MessageBox]::Show(
                $form,
                'Secret and Key are required.',
                'Credentials required',
                [Windows.Forms.MessageBoxButtons]::OK,
                [Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }

        New-Item -ItemType Directory -Path $script:AppRoot -Force | Out-Null
        [pscustomobject]@{
            ApiUser = $apiUser
            Secret = $secret
            Key = $key
            SavedAt = (Get-Date).ToUniversalTime()
        } | Export-Clixml -LiteralPath $script:CredentialPath -Force

        $script:credentialSaved = $true
        $form.DialogResult = [Windows.Forms.DialogResult]::OK
        $form.Close()
    })

    [void]$form.ShowDialog()
    $form.Dispose()

    if ($RequireCredentials -and -not $script:credentialSaved -and -not (Test-Path -LiteralPath $script:CredentialPath)) {
        return $false
    }

    return $script:credentialSaved
}

function Select-ImportOptions {
    param(
        [Parameter(Mandatory)]$Mappings,
        [string]$InitialFile
    )

    $script:mailchimpReview = $null
    $script:dialogSelection = $null
    $script:activeMappings = $Mappings

    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Kallman Mailchimp to Momentus Sync'
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedSingle
    $form.MaximizeBox = $false
    $form.MinimizeBox = $true
    $form.ClientSize = [Drawing.Size]::new(850, 715)
    $form.BackColor = $script:LightGray
    $form.Font = [Drawing.Font]::new('Segoe UI', 9)

    $header = [Windows.Forms.Panel]::new()
    $header.Dock = [Windows.Forms.DockStyle]::Top
    $header.Height = 92
    $header.BackColor = $script:Navy
    $form.Controls.Add($header)

    $title = [Windows.Forms.Label]::new()
    $title.Text = 'MAILCHIMP TO MOMENTUS SYNC'
    $title.Font = [Drawing.Font]::new('Segoe UI', 18, [Drawing.FontStyle]::Bold)
    $title.ForeColor = [Drawing.Color]::White
    $title.AutoSize = $true
    $title.Location = [Drawing.Point]::new(25, 18)
    $header.Controls.Add($title)

    $subtitle = [Windows.Forms.Label]::new()
    $subtitle.Text = 'Select the raw Mailchimp file, enter the campaign name and sent date, then choose the campaign type and salesperson.'
    $subtitle.Font = [Drawing.Font]::new('Segoe UI', 9.5)
    $subtitle.ForeColor = [Drawing.Color]::White
    $subtitle.AutoSize = $true
    $subtitle.Location = [Drawing.Point]::new(28, 57)
    $header.Controls.Add($subtitle)

    $manageListsButton = [Windows.Forms.Button]::new()
    $manageListsButton.Text = 'Manage Lists'
    $manageListsButton.Location = [Drawing.Point]::new(590, 29)
    $manageListsButton.Size = [Drawing.Size]::new(112, 34)
    $manageListsButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $manageListsButton.FlatAppearance.BorderColor = [Drawing.Color]::White
    $manageListsButton.ForeColor = [Drawing.Color]::White
    $manageListsButton.BackColor = $script:Navy
    $header.Controls.Add($manageListsButton)

    $settingsButton = [Windows.Forms.Button]::new()
    $settingsButton.Text = 'API Settings'
    $settingsButton.Location = [Drawing.Point]::new(712, 29)
    $settingsButton.Size = [Drawing.Size]::new(110, 34)
    $settingsButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $settingsButton.FlatAppearance.BorderColor = [Drawing.Color]::White
    $settingsButton.ForeColor = [Drawing.Color]::White
    $settingsButton.BackColor = $script:Navy
    $header.Controls.Add($settingsButton)

    $accent = [Windows.Forms.Panel]::new()
    $accent.Location = [Drawing.Point]::new(0, 92)
    $accent.Size = [Drawing.Size]::new(850, 5)
    $accent.BackColor = $script:Red
    $form.Controls.Add($accent)
    $accent.BringToFront()

    $instruction = [Windows.Forms.Label]::new()
    $instruction.Text = 'Mailchimp supplies Email, First Name, Last Name, Opens, Clicks, and Account Code. Enter the Campaign name and Mailchimp sent date. Campaign Type and Salesperson come from the dropdown catalog.'
    $instruction.AutoSize = $false
    $instruction.Size = [Drawing.Size]::new(800, 42)
    $instruction.Location = [Drawing.Point]::new(25, 112)
    $instruction.ForeColor = $script:Text
    $form.Controls.Add($instruction)

    $fileLabel = [Windows.Forms.Label]::new()
    $fileLabel.Text = 'MAILCHIMP CSV'
    $fileLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $fileLabel.ForeColor = $script:Navy
    $fileLabel.AutoSize = $true
    $fileLabel.Location = [Drawing.Point]::new(25, 164)
    $form.Controls.Add($fileLabel)

    $fileTextBox = [Windows.Forms.TextBox]::new()
    $fileTextBox.ReadOnly = $true
    $fileTextBox.Location = [Drawing.Point]::new(25, 187)
    $fileTextBox.Size = [Drawing.Size]::new(680, 25)
    $fileTextBox.BackColor = [Drawing.Color]::White
    $form.Controls.Add($fileTextBox)

    $browseButton = [Windows.Forms.Button]::new()
    $browseButton.Text = 'Browse...'
    $browseButton.Location = [Drawing.Point]::new(716, 184)
    $browseButton.Size = [Drawing.Size]::new(108, 31)
    $browseButton.BackColor = $script:Blue
    $browseButton.ForeColor = [Drawing.Color]::White
    $browseButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $browseButton.FlatAppearance.BorderSize = 0
    $form.Controls.Add($browseButton)

    $eventLabel = [Windows.Forms.Label]::new()
    $eventLabel.Text = 'EVENT ID (4 DIGITS)'
    $eventLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $eventLabel.ForeColor = $script:Navy
    $eventLabel.AutoSize = $true
    $eventLabel.Location = [Drawing.Point]::new(25, 237)
    $form.Controls.Add($eventLabel)

    $eventTextBox = [Windows.Forms.TextBox]::new()
    $eventTextBox.Location = [Drawing.Point]::new(25, 260)
    $eventTextBox.Size = [Drawing.Size]::new(170, 25)
    $eventTextBox.MaxLength = 4
    $form.Controls.Add($eventTextBox)

    $campaignTitleLabel = [Windows.Forms.Label]::new()
    $campaignTitleLabel.Text = 'CAMPAIGN'
    $campaignTitleLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $campaignTitleLabel.ForeColor = $script:Navy
    $campaignTitleLabel.AutoSize = $true
    $campaignTitleLabel.Location = [Drawing.Point]::new(220, 237)
    $form.Controls.Add($campaignTitleLabel)

    $campaignTitleTextBox = [Windows.Forms.TextBox]::new()
    $campaignTitleTextBox.Location = [Drawing.Point]::new(220, 260)
    $campaignTitleTextBox.Size = [Drawing.Size]::new(365, 25)
    $campaignTitleTextBox.MaxLength = 60
    $form.Controls.Add($campaignTitleTextBox)

    $sentDateLabel = [Windows.Forms.Label]::new()
    $sentDateLabel.Text = 'CAMPAIGN SENT DATE'
    $sentDateLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $sentDateLabel.ForeColor = $script:Navy
    $sentDateLabel.AutoSize = $true
    $sentDateLabel.Location = [Drawing.Point]::new(610, 237)
    $form.Controls.Add($sentDateLabel)

    $sentDatePicker = [Windows.Forms.DateTimePicker]::new()
    $sentDatePicker.Location = [Drawing.Point]::new(610, 260)
    $sentDatePicker.Size = [Drawing.Size]::new(214, 25)
    $sentDatePicker.Format = [Windows.Forms.DateTimePickerFormat]::Custom
    $sentDatePicker.CustomFormat = 'MMMM d, yyyy'
    $sentDatePicker.MaxDate = [DateTime]::Today
    $sentDatePicker.Value = [DateTime]::Today
    $form.Controls.Add($sentDatePicker)

    $campaignLabel = [Windows.Forms.Label]::new()
    $campaignLabel.Text = 'CAMPAIGN TYPE'
    $campaignLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $campaignLabel.ForeColor = $script:Navy
    $campaignLabel.AutoSize = $true
    $campaignLabel.Location = [Drawing.Point]::new(25, 305)
    $form.Controls.Add($campaignLabel)

    $campaignCombo = [Windows.Forms.ComboBox]::new()
    $campaignCombo.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
    $campaignCombo.Location = [Drawing.Point]::new(25, 328)
    $campaignCombo.Size = [Drawing.Size]::new(365, 25)
    $campaignCombo.DropDownWidth = 365
    foreach ($campaign in $script:activeMappings.CampaignTypes) {
        [void]$campaignCombo.Items.Add([string]$campaign.CampaignType)
    }
    $campaignCombo.SelectedIndex = -1
    $form.Controls.Add($campaignCombo)

    $salespersonLabel = [Windows.Forms.Label]::new()
    $salespersonLabel.Text = 'SALESPERSON'
    $salespersonLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $salespersonLabel.ForeColor = $script:Navy
    $salespersonLabel.AutoSize = $true
    $salespersonLabel.Location = [Drawing.Point]::new(414, 305)
    $form.Controls.Add($salespersonLabel)

    $salespersonCombo = [Windows.Forms.ComboBox]::new()
    $salespersonCombo.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
    $salespersonCombo.Location = [Drawing.Point]::new(414, 328)
    $salespersonCombo.Size = [Drawing.Size]::new(410, 25)
    $salespersonCombo.DropDownWidth = 410
    foreach ($salesperson in $script:activeMappings.Salespeople) {
        [void]$salespersonCombo.Items.Add([string]$salesperson.DisplayName)
    }
    $salespersonCombo.SelectedIndex = -1
    $form.Controls.Add($salespersonCombo)

    $activityLabel = [Windows.Forms.Label]::new()
    $activityLabel.Text = 'ACTIVITY TYPES (SET BY CAMPAIGN TYPE)'
    $activityLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $activityLabel.ForeColor = $script:Navy
    $activityLabel.AutoSize = $true
    $activityLabel.Location = [Drawing.Point]::new(25, 370)
    $form.Controls.Add($activityLabel)

    $activityTextBox = [Windows.Forms.TextBox]::new()
    $activityTextBox.ReadOnly = $true
    $activityTextBox.Location = [Drawing.Point]::new(25, 393)
    $activityTextBox.Size = [Drawing.Size]::new(365, 25)
    $activityTextBox.BackColor = [Drawing.Color]::White
    $activityTextBox.Text = 'Select a campaign type to load the activity codes.'
    $form.Controls.Add($activityTextBox)

    $salesCodeLabel = [Windows.Forms.Label]::new()
    $salesCodeLabel.Text = 'SALESPERSON ACCOUNT CODE'
    $salesCodeLabel.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $salesCodeLabel.ForeColor = $script:Navy
    $salesCodeLabel.AutoSize = $true
    $salesCodeLabel.Location = [Drawing.Point]::new(414, 370)
    $form.Controls.Add($salesCodeLabel)

    $salesCodeTextBox = [Windows.Forms.TextBox]::new()
    $salesCodeTextBox.ReadOnly = $true
    $salesCodeTextBox.Location = [Drawing.Point]::new(414, 393)
    $salesCodeTextBox.Size = [Drawing.Size]::new(410, 25)
    $salesCodeTextBox.BackColor = [Drawing.Color]::White
    $salesCodeTextBox.Text = 'Select a salesperson to load the account code.'
    $form.Controls.Add($salesCodeTextBox)

    $previewGroup = [Windows.Forms.GroupBox]::new()
    $previewGroup.Text = 'CAMPAIGN AND CLICK-ACTIVITY REVIEW'
    $previewGroup.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $previewGroup.ForeColor = $script:Navy
    $previewGroup.Location = [Drawing.Point]::new(25, 442)
    $previewGroup.Size = [Drawing.Size]::new(799, 165)
    $previewGroup.BackColor = [Drawing.Color]::White
    $form.Controls.Add($previewGroup)

    $previewFile = [Windows.Forms.Label]::new()
    $previewFile.Text = 'Choose a CSV file to review it.'
    $previewFile.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Regular)
    $previewFile.ForeColor = $script:Text
    $previewFile.AutoSize = $false
    $previewFile.Size = [Drawing.Size]::new(750, 25)
    $previewFile.Location = [Drawing.Point]::new(18, 27)
    $previewGroup.Controls.Add($previewFile)

    $previewRows = [Windows.Forms.Label]::new()
    $previewRows.Text = 'Total data rows: -'
    $previewRows.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Regular)
    $previewRows.ForeColor = $script:Text
    $previewRows.AutoSize = $true
    $previewRows.Location = [Drawing.Point]::new(18, 60)
    $previewGroup.Controls.Add($previewRows)

    $previewEligible = [Windows.Forms.Label]::new()
    $previewEligible.Text = 'Engaged campaign contacts: -'
    $previewEligible.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Bold)
    $previewEligible.ForeColor = $script:Success
    $previewEligible.AutoSize = $true
    $previewEligible.Location = [Drawing.Point]::new(255, 60)
    $previewGroup.Controls.Add($previewEligible)

    $previewNoClick = [Windows.Forms.Label]::new()
    $previewNoClick.Text = 'Activity/note recipients (clicked): -'
    $previewNoClick.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Regular)
    $previewNoClick.ForeColor = $script:Text
    $previewNoClick.AutoSize = $true
    $previewNoClick.Location = [Drawing.Point]::new(18, 94)
    $previewGroup.Controls.Add($previewNoClick)

    $previewMissing = [Windows.Forms.Label]::new()
    $previewMissing.Text = 'Rows missing Account Code: -'
    $previewMissing.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Regular)
    $previewMissing.ForeColor = $script:Text
    $previewMissing.AutoSize = $true
    $previewMissing.Location = [Drawing.Point]::new(390, 94)
    $previewGroup.Controls.Add($previewMissing)

    $previewStatuses = [Windows.Forms.Label]::new()
    $previewStatuses.Text = 'Campaign statuses: Opened - | Opened and Clicked -'
    $previewStatuses.Font = [Drawing.Font]::new('Segoe UI', 9, [Drawing.FontStyle]::Regular)
    $previewStatuses.ForeColor = $script:Text
    $previewStatuses.AutoSize = $true
    $previewStatuses.Location = [Drawing.Point]::new(18, 128)
    $previewGroup.Controls.Add($previewStatuses)

    $cancelButton = [Windows.Forms.Button]::new()
    $cancelButton.Text = 'Cancel'
    $cancelButton.Location = [Drawing.Point]::new(610, 638)
    $cancelButton.Size = [Drawing.Size]::new(100, 38)
    $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelButton)

    $runButton = [Windows.Forms.Button]::new()
    $runButton.Text = 'Run Import'
    $runButton.Location = [Drawing.Point]::new(724, 638)
    $runButton.Size = [Drawing.Size]::new(100, 38)
    $runButton.Enabled = $false
    $runButton.BackColor = $script:Red
    $runButton.ForeColor = [Drawing.Color]::White
    $runButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $runButton.FlatAppearance.BorderSize = 0
    $form.Controls.Add($runButton)

    $catalogLabel = [Windows.Forms.Label]::new()
    $catalogLabel.Text = 'Dropdown catalog: {0} | {1} campaign(s) | {2} salesperson mapping(s)' -f $script:activeMappings.SourceKind, $script:activeMappings.CampaignTypes.Count, $script:activeMappings.Salespeople.Count
    $catalogLabel.ForeColor = $script:Muted
    $catalogLabel.AutoSize = $true
    $catalogLabel.Location = [Drawing.Point]::new(25, 651)
    $form.Controls.Add($catalogLabel)

    $form.CancelButton = $cancelButton

    $applyMappingsToControls = {
        param($NewMappings)

        $script:activeMappings = $NewMappings
        $campaignCombo.Items.Clear()
        foreach ($campaignItem in $script:activeMappings.CampaignTypes) {
            [void]$campaignCombo.Items.Add([string]$campaignItem.CampaignType)
        }
        $campaignCombo.SelectedIndex = -1
        $activityTextBox.Text = 'Select a campaign type to load the activity codes.'

        $salespersonCombo.Items.Clear()
        foreach ($salespersonItem in $script:activeMappings.Salespeople) {
            [void]$salespersonCombo.Items.Add([string]$salespersonItem.DisplayName)
        }
        $salespersonCombo.SelectedIndex = -1
        $salesCodeTextBox.Text = 'Select a salesperson to load the account code.'

        $catalogLabel.Text = ('Dropdown catalog: {0} | {1} campaign(s) | {2} salesperson mapping(s)' -f $script:activeMappings.SourceKind, $script:activeMappings.CampaignTypes.Count, $script:activeMappings.Salespeople.Count)
    }

    $refreshRunState = {
        $hasValidEvent = $eventTextBox.Text -match '^[1-9]\d{3}$'
        $hasCampaignTitle = -not [string]::IsNullOrWhiteSpace($campaignTitleTextBox.Text)
        $runButton.Enabled =
            $null -ne $script:mailchimpReview -and
            $script:mailchimpReview.CampaignRowCount -gt 0 -and
            $hasValidEvent -and
            $hasCampaignTitle -and
            $campaignCombo.SelectedIndex -ge 0 -and
            $salespersonCombo.SelectedIndex -ge 0
    }

    $loadSelectedFile = {
        param([string]$Path)

        try {
            $review = Read-RawMailchimpCsv -Path $Path
            $script:mailchimpReview = $review
            $fileTextBox.Text = $review.Path
            $previewFile.Text = [IO.Path]::GetFileName($review.Path)
            $previewRows.Text = 'Total data rows: ' + $review.TotalRows
            $previewEligible.Text = 'Engaged campaign contacts: ' + $review.EngagedCampaignRowCount
            $previewNoClick.Text = 'Activity/note recipients (clicked): ' + $review.EligibleRowCount
            $previewMissing.Text = 'Rows missing Account Code: ' + $review.SkippedMissingAccount
            $previewStatuses.Text = 'Campaign statuses: Opened {0} | Opened and Clicked {1} | Ignored no interaction {2}' -f $review.OpenedCount, $review.OpenedClickedCount, $review.NoInteractionCount
        }
        catch {
            $script:mailchimpReview = $null
            $fileTextBox.Clear()
            $previewFile.Text = 'Choose a valid Mailchimp CSV file.'
            $previewRows.Text = 'Total data rows: -'
            $previewEligible.Text = 'Engaged campaign contacts: -'
            $previewNoClick.Text = 'Activity/note recipients (clicked): -'
            $previewMissing.Text = 'Rows missing Account Code: -'
            $previewStatuses.Text = 'Campaign statuses: Opened - | Opened and Clicked -'
            Show-AppError $_.Exception.Message 'Mailchimp file could not be used'
        }

        & $refreshRunState
    }

    $browseButton.Add_Click({
        $dialog = [Windows.Forms.OpenFileDialog]::new()
        $dialog.Title = 'Choose the raw Mailchimp CSV file'
        $dialog.Filter = 'CSV files (*.csv)|*.csv'
        $dialog.Multiselect = $false
        $dialog.CheckFileExists = $true
        try {
            if ($dialog.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) {
                & $loadSelectedFile $dialog.FileName
            }
        }
        finally {
            $dialog.Dispose()
        }
    })

    $manageListsButton.Add_Click({
        try {
            $updatedMappings = Show-MappingEditor -Mappings $script:activeMappings
            if ($null -ne $updatedMappings) {
                & $applyMappingsToControls $updatedMappings
                & $refreshRunState
                [Windows.Forms.MessageBox]::Show(
                    $form,
                    'The dropdown lists were saved and reloaded.',
                    'Dropdown lists updated',
                    [Windows.Forms.MessageBoxButtons]::OK,
                    [Windows.Forms.MessageBoxIcon]::Information) | Out-Null
            }
        }
        catch {
            Show-AppError $_.Exception.Message 'Dropdown lists could not be opened'
        }
    })

    $settingsButton.Add_Click({
        [void](Show-CredentialDialog)
    })

    $eventTextBox.Add_KeyPress({
        param($sender, $eventArgs)
        if (-not [char]::IsControl($eventArgs.KeyChar) -and -not [char]::IsDigit($eventArgs.KeyChar)) {
            $eventArgs.Handled = $true
        }
    })
    $eventTextBox.Add_TextChanged({ & $refreshRunState })
    $campaignTitleTextBox.Add_TextChanged({ & $refreshRunState })

    $campaignCombo.Add_SelectedIndexChanged({
        if ($campaignCombo.SelectedIndex -ge 0) {
            $selectedCampaign = $script:activeMappings.CampaignTypes[$campaignCombo.SelectedIndex]
            $activityTextBox.Text = 'Click: {0}    |    Open: {1}' -f $selectedCampaign.ClickType, $selectedCampaign.OpenType
        }
        else {
            $activityTextBox.Text = 'Select a campaign type to load the activity codes.'
        }
        & $refreshRunState
    })

    $salespersonCombo.Add_SelectedIndexChanged({
        if ($salespersonCombo.SelectedIndex -ge 0) {
            $selectedSalesperson = $script:activeMappings.Salespeople[$salespersonCombo.SelectedIndex]
            $salesCodeTextBox.Text = [string]$selectedSalesperson.SalespersonAccountCode
        }
        else {
            $salesCodeTextBox.Text = 'Select a salesperson to load the account code.'
        }
        & $refreshRunState
    })

    $runButton.Add_Click({
        if (-not $runButton.Enabled) {
            return
        }

        $campaign = $script:activeMappings.CampaignTypes[$campaignCombo.SelectedIndex]
        $salesperson = $script:activeMappings.Salespeople[$salespersonCombo.SelectedIndex]
        $confirmText = @"
This will make LIVE changes in Momentus.

Event ID: $($eventTextBox.Text)
Campaign: $($campaignTitleTextBox.Text.Trim())
Campaign Sent Date: $($sentDatePicker.Value.ToString('MMMM d, yyyy'))
Campaign Type: $([string]$campaign.CampaignType)
Click Activity Type: $([string]$campaign.ClickType)
Open Activity Type: $([string]$campaign.OpenType)
Salesperson: $([string]$salesperson.Salesperson)
Salesperson Account Code: $([string]$salesperson.SalespersonAccountCode)
Clicked records to import: $($script:mailchimpReview.EligibleRowCount)
Engaged campaign contacts to add: $($script:mailchimpReview.EngagedCampaignRowCount)
No Interaction ignored: $($script:mailchimpReview.NoInteractionCount)
Opened (OPE): $($script:mailchimpReview.OpenedCount)
Opened and Clicked (CLI): $($script:mailchimpReview.OpenedClickedCount)
Campaign response percentage: $($script:mailchimpReview.ResponsePercentage)%

Continue with the live import?
"@
        $answer = [Windows.Forms.MessageBox]::Show(
            $form,
            $confirmText,
            'Confirm live Momentus import',
            [Windows.Forms.MessageBoxButtons]::YesNo,
            [Windows.Forms.MessageBoxIcon]::Warning)

        if ($answer -ne [Windows.Forms.DialogResult]::Yes) {
            return
        }

        $script:dialogSelection = [pscustomobject]@{
            Review = $script:mailchimpReview
            EventId = [int]$eventTextBox.Text
            CampaignTitle = $campaignTitleTextBox.Text.Trim()
            CampaignSentDate = $sentDatePicker.Value.Date
            Campaign = $campaign
            Salesperson = $salesperson
        }
        $form.DialogResult = [Windows.Forms.DialogResult]::OK
        $form.Close()
    })

    if (-not [string]::IsNullOrWhiteSpace($InitialFile)) {
        $form.Add_Shown({ & $loadSelectedFile $InitialFile })
    }

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:dialogSelection
}

function Select-ImportQueueOptions {
    param(
        [Parameter(Mandatory)]$Mappings,
        [string]$InitialFile
    )

    $script:batchDialogSelections = $null
    $script:activeMappings = $Mappings

    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Kallman Mailchimp to Momentus Sync - Batch Queue'
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::Sizable
    $form.MinimumSize = [Drawing.Size]::new(1120, 620)
    $form.ClientSize = [Drawing.Size]::new(1370, 720)
    $form.BackColor = $script:LightGray
    $form.Font = [Drawing.Font]::new('Segoe UI', 9)

    $header = [Windows.Forms.Panel]::new()
    $header.Dock = [Windows.Forms.DockStyle]::Top
    $header.Height = 92
    $header.BackColor = $script:Navy
    $form.Controls.Add($header)

    $title = [Windows.Forms.Label]::new()
    $title.Text = 'MAILCHIMP IMPORT QUEUE'
    $title.Font = [Drawing.Font]::new('Segoe UI', 18, [Drawing.FontStyle]::Bold)
    $title.ForeColor = [Drawing.Color]::White
    $title.AutoSize = $true
    $title.Location = [Drawing.Point]::new(24, 17)
    $header.Controls.Add($title)

    $subtitle = [Windows.Forms.Label]::new()
    $subtitle.Text = 'Add one row per campaign. Files run one at a time, in order, and the queue stops if a row fails.'
    $subtitle.Font = [Drawing.Font]::new('Segoe UI', 9.5)
    $subtitle.ForeColor = [Drawing.Color]::White
    $subtitle.AutoSize = $true
    $subtitle.Location = [Drawing.Point]::new(27, 57)
    $header.Controls.Add($subtitle)

    $manageListsButton = [Windows.Forms.Button]::new()
    $manageListsButton.Text = 'Manage Lists'
    $manageListsButton.Anchor = [Windows.Forms.AnchorStyles]::Top -bor [Windows.Forms.AnchorStyles]::Right
    $manageListsButton.Location = [Drawing.Point]::new(1110, 29)
    $manageListsButton.Size = [Drawing.Size]::new(112, 34)
    $manageListsButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $manageListsButton.FlatAppearance.BorderColor = [Drawing.Color]::White
    $manageListsButton.ForeColor = [Drawing.Color]::White
    $manageListsButton.BackColor = $script:Navy
    $header.Controls.Add($manageListsButton)

    $settingsButton = [Windows.Forms.Button]::new()
    $settingsButton.Text = 'API Settings'
    $settingsButton.Anchor = [Windows.Forms.AnchorStyles]::Top -bor [Windows.Forms.AnchorStyles]::Right
    $settingsButton.Location = [Drawing.Point]::new(1232, 29)
    $settingsButton.Size = [Drawing.Size]::new(110, 34)
    $settingsButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $settingsButton.FlatAppearance.BorderColor = [Drawing.Color]::White
    $settingsButton.ForeColor = [Drawing.Color]::White
    $settingsButton.BackColor = $script:Navy
    $header.Controls.Add($settingsButton)

    $accent = [Windows.Forms.Panel]::new()
    $accent.Dock = [Windows.Forms.DockStyle]::Top
    $accent.Height = 5
    $accent.BackColor = $script:Red
    $form.Controls.Add($accent)
    $accent.BringToFront()

    $instruction = [Windows.Forms.Label]::new()
    $instruction.Text = 'Use the buttons inside each row to choose the CSV and sent date. Campaign name, Event ID, Campaign Type, and Salesperson are required for every row.'
    $instruction.AutoSize = $false
    $instruction.Anchor = [Windows.Forms.AnchorStyles]::Top -bor [Windows.Forms.AnchorStyles]::Left -bor [Windows.Forms.AnchorStyles]::Right
    $instruction.Size = [Drawing.Size]::new(1318, 40)
    $instruction.Location = [Drawing.Point]::new(24, 111)
    $instruction.ForeColor = $script:Text
    $form.Controls.Add($instruction)

    $grid = [Windows.Forms.DataGridView]::new()
    $grid.Location = [Drawing.Point]::new(24, 158)
    $grid.Size = [Drawing.Size]::new(1318, 430)
    $grid.Anchor = [Windows.Forms.AnchorStyles]::Top -bor [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left -bor [Windows.Forms.AnchorStyles]::Right
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToDeleteRows = $false
    $grid.AllowUserToResizeRows = $false
    $grid.AutoGenerateColumns = $false
    $grid.BackgroundColor = [Drawing.Color]::White
    $grid.BorderStyle = [Windows.Forms.BorderStyle]::FixedSingle
    $grid.ColumnHeadersHeight = 38
    $grid.ColumnHeadersHeightSizeMode = [Windows.Forms.DataGridViewColumnHeadersHeightSizeMode]::DisableResizing
    $grid.EditMode = [Windows.Forms.DataGridViewEditMode]::EditOnEnter
    $grid.RowHeadersVisible = $false
    $grid.RowTemplate.Height = 44
    $grid.SelectionMode = [Windows.Forms.DataGridViewSelectionMode]::CellSelect
    $grid.MultiSelect = $false
    $grid.AutoSizeRowsMode = [Windows.Forms.DataGridViewAutoSizeRowsMode]::None
    $grid.DefaultCellStyle.WrapMode = [Windows.Forms.DataGridViewTriState]::False
    $grid.AlternatingRowsDefaultCellStyle.BackColor = $script:LightGray
    $form.Controls.Add($grid)

    $fileColumn = [Windows.Forms.DataGridViewButtonColumn]::new()
    $fileColumn.Name = 'File'
    $fileColumn.HeaderText = '1. MAILCHIMP CSV'
    $fileColumn.Width = 225
    $fileColumn.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $fileColumn.UseColumnTextForButtonValue = $false
    [void]$grid.Columns.Add($fileColumn)

    $campaignTitleColumn = [Windows.Forms.DataGridViewTextBoxColumn]::new()
    $campaignTitleColumn.Name = 'CampaignTitle'
    $campaignTitleColumn.HeaderText = '2. CAMPAIGN NAME'
    $campaignTitleColumn.Width = 185
    $campaignTitleColumn.MaxInputLength = 60
    [void]$grid.Columns.Add($campaignTitleColumn)

    $sentDateColumn = [Windows.Forms.DataGridViewButtonColumn]::new()
    $sentDateColumn.Name = 'SentDate'
    $sentDateColumn.HeaderText = '3. SENT DATE'
    $sentDateColumn.Width = 108
    $sentDateColumn.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $sentDateColumn.UseColumnTextForButtonValue = $false
    [void]$grid.Columns.Add($sentDateColumn)

    $eventColumn = [Windows.Forms.DataGridViewTextBoxColumn]::new()
    $eventColumn.Name = 'EventId'
    $eventColumn.HeaderText = '4. EVENT ID'
    $eventColumn.Width = 78
    $eventColumn.MaxInputLength = 4
    [void]$grid.Columns.Add($eventColumn)

    $campaignTypeColumn = [Windows.Forms.DataGridViewComboBoxColumn]::new()
    $campaignTypeColumn.Name = 'CampaignType'
    $campaignTypeColumn.HeaderText = '5. CAMPAIGN TYPE'
    $campaignTypeColumn.Width = 160
    $campaignTypeColumn.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    foreach ($campaignItem in $script:activeMappings.CampaignTypes) {
        [void]$campaignTypeColumn.Items.Add([string]$campaignItem.CampaignType)
    }
    [void]$grid.Columns.Add($campaignTypeColumn)

    $salespersonColumn = [Windows.Forms.DataGridViewComboBoxColumn]::new()
    $salespersonColumn.Name = 'Salesperson'
    $salespersonColumn.HeaderText = '6. SALESPERSON'
    $salespersonColumn.Width = 185
    $salespersonColumn.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    foreach ($salespersonItem in $script:activeMappings.Salespeople) {
        [void]$salespersonColumn.Items.Add([string]$salespersonItem.DisplayName)
    }
    [void]$grid.Columns.Add($salespersonColumn)

    $reviewColumn = [Windows.Forms.DataGridViewTextBoxColumn]::new()
    $reviewColumn.Name = 'Review'
    $reviewColumn.HeaderText = 'FILE REVIEW'
    $reviewColumn.Width = 270
    $reviewColumn.ReadOnly = $true
    [void]$grid.Columns.Add($reviewColumn)

    $removeColumn = [Windows.Forms.DataGridViewButtonColumn]::new()
    $removeColumn.Name = 'Remove'
    $removeColumn.HeaderText = ''
    $removeColumn.Width = 72
    $removeColumn.Text = 'Remove'
    $removeColumn.UseColumnTextForButtonValue = $true
    $removeColumn.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    [void]$grid.Columns.Add($removeColumn)

    $addButton = [Windows.Forms.Button]::new()
    $addButton.Text = '+ Add another file'
    $addButton.Location = [Drawing.Point]::new(24, 606)
    $addButton.Size = [Drawing.Size]::new(155, 38)
    $addButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $addButton.BackColor = $script:Blue
    $addButton.ForeColor = [Drawing.Color]::White
    $addButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $addButton.FlatAppearance.BorderSize = 0
    $form.Controls.Add($addButton)

    $queueLabel = [Windows.Forms.Label]::new()
    $queueLabel.Text = 'Queue: 0 files'
    $queueLabel.Location = [Drawing.Point]::new(194, 616)
    $queueLabel.AutoSize = $true
    $queueLabel.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $queueLabel.ForeColor = $script:Muted
    $form.Controls.Add($queueLabel)

    $cancelButton = [Windows.Forms.Button]::new()
    $cancelButton.Text = 'Cancel'
    $cancelButton.Location = [Drawing.Point]::new(1118, 606)
    $cancelButton.Size = [Drawing.Size]::new(100, 38)
    $cancelButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Right
    $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelButton)

    $runButton = [Windows.Forms.Button]::new()
    $runButton.Text = 'Run Queue'
    $runButton.Location = [Drawing.Point]::new(1232, 606)
    $runButton.Size = [Drawing.Size]::new(110, 38)
    $runButton.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Right
    $runButton.Enabled = $false
    $runButton.BackColor = $script:Red
    $runButton.ForeColor = [Drawing.Color]::White
    $runButton.FlatStyle = [Windows.Forms.FlatStyle]::Flat
    $runButton.FlatAppearance.BorderSize = 0
    $form.Controls.Add($runButton)

    $catalogLabel = [Windows.Forms.Label]::new()
    $catalogLabel.Text = 'Dropdown catalog: {0} | {1} campaign(s) | {2} salesperson mapping(s)' -f $script:activeMappings.SourceKind, $script:activeMappings.CampaignTypes.Count, $script:activeMappings.Salespeople.Count
    $catalogLabel.ForeColor = $script:Muted
    $catalogLabel.AutoSize = $true
    $catalogLabel.Location = [Drawing.Point]::new(24, 670)
    $catalogLabel.Anchor = [Windows.Forms.AnchorStyles]::Bottom -bor [Windows.Forms.AnchorStyles]::Left
    $form.Controls.Add($catalogLabel)

    $form.CancelButton = $cancelButton

    $addBlankRow = {
        $previousRow = if ($grid.Rows.Count -gt 0) { $grid.Rows[$grid.Rows.Count - 1] } else { $null }
        $rowIndex = $grid.Rows.Add()
        $row = $grid.Rows[$rowIndex]
        $row.Tag = [pscustomobject]@{ Path = ''; Review = $null }
        $row.Cells['File'].Value = 'Choose file...'
        $row.Cells['SentDate'].Value = 'Choose date...'
        $row.Cells['Review'].Value = 'Waiting for a CSV file'
        $row.Cells['Review'].Style.ForeColor = $script:Muted
        if ($null -ne $previousRow) {
            $row.Cells['EventId'].Value = $previousRow.Cells['EventId'].Value
            $row.Cells['Salesperson'].Value = $previousRow.Cells['Salesperson'].Value
        }
        return $row
    }

    $refreshRunState = {
        $ready = $grid.Rows.Count -gt 0
        $readyCount = 0
        foreach ($row in $grid.Rows) {
            $rowReady =
                $null -ne $row.Tag.Review -and
                -not [string]::IsNullOrWhiteSpace([string]$row.Cells['CampaignTitle'].Value) -and
                ([string]$row.Cells['CampaignTitle'].Value).Trim().Length -le 60 -and
                [string]$row.Cells['SentDate'].Value -match '^\d{4}-\d{2}-\d{2}$' -and
                [string]$row.Cells['EventId'].Value -match '^[1-9]\d{3}$' -and
                -not [string]::IsNullOrWhiteSpace([string]$row.Cells['CampaignType'].Value) -and
                -not [string]::IsNullOrWhiteSpace([string]$row.Cells['Salesperson'].Value)
            if ($rowReady) {
                $readyCount++
            }
            else {
                $ready = $false
            }
        }
        $queueLabel.Text = 'Queue: {0} file(s) | {1} ready' -f $grid.Rows.Count, $readyCount
        $runButton.Enabled = $ready
    }

    $loadFileIntoRow = {
        param($Row, [string]$Path)

        try {
            $review = Read-RawMailchimpCsv -Path $Path
            $Row.Tag = [pscustomobject]@{ Path = $review.Path; Review = $review }
            $Row.Cells['File'].Value = [IO.Path]::GetFileName($review.Path)
            $Row.Cells['File'].ToolTipText = $review.Path
            $Row.Cells['Review'].Value = '{0:N0} rows | {1:N0} engaged | {2:N0} clicked' -f $review.TotalRows, $review.EngagedCampaignRowCount, $review.EligibleRowCount
            $Row.Cells['Review'].ToolTipText = 'Opened: {0:N0}; Opened and Clicked: {1:N0}; No interaction ignored: {2:N0}; Missing Account Code: {3:N0}' -f $review.OpenedCount, $review.OpenedClickedCount, $review.NoInteractionCount, $review.SkippedMissingAccount
            $Row.Cells['Review'].Style.ForeColor = $script:Success
        }
        catch {
            $Row.Tag = [pscustomobject]@{ Path = ''; Review = $null }
            $Row.Cells['File'].Value = 'Choose file...'
            $Row.Cells['File'].ToolTipText = ''
            $Row.Cells['Review'].Value = 'File could not be used'
            $Row.Cells['Review'].Style.ForeColor = $script:Red
            Show-AppError $_.Exception.Message 'Mailchimp file could not be used'
        }
        & $refreshRunState
    }

    $chooseDateForRow = {
        param($Row)

        $dateForm = [Windows.Forms.Form]::new()
        $dateForm.Text = 'Choose campaign sent date'
        $dateForm.StartPosition = [Windows.Forms.FormStartPosition]::CenterParent
        $dateForm.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
        $dateForm.MaximizeBox = $false
        $dateForm.MinimizeBox = $false
        $dateForm.ClientSize = [Drawing.Size]::new(360, 145)
        $dateForm.Font = [Drawing.Font]::new('Segoe UI', 9)

        $dateLabel = [Windows.Forms.Label]::new()
        $dateLabel.Text = 'Mailchimp sent date:'
        $dateLabel.AutoSize = $true
        $dateLabel.Location = [Drawing.Point]::new(22, 22)
        $dateForm.Controls.Add($dateLabel)

        $picker = [Windows.Forms.DateTimePicker]::new()
        $picker.Location = [Drawing.Point]::new(22, 48)
        $picker.Size = [Drawing.Size]::new(315, 25)
        $picker.Format = [Windows.Forms.DateTimePickerFormat]::Long
        $picker.MaxDate = [DateTime]::Today
        $existingDate = [datetime]::MinValue
        if ([datetime]::TryParseExact(
            [string]$Row.Cells['SentDate'].Value,
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$existingDate)) {
            $picker.Value = $existingDate
        }
        else {
            $picker.Value = [DateTime]::Today
        }
        $dateForm.Controls.Add($picker)

        $dateCancel = [Windows.Forms.Button]::new()
        $dateCancel.Text = 'Cancel'
        $dateCancel.Location = [Drawing.Point]::new(151, 94)
        $dateCancel.Size = [Drawing.Size]::new(88, 32)
        $dateCancel.DialogResult = [Windows.Forms.DialogResult]::Cancel
        $dateForm.Controls.Add($dateCancel)

        $dateOk = [Windows.Forms.Button]::new()
        $dateOk.Text = 'Use Date'
        $dateOk.Location = [Drawing.Point]::new(249, 94)
        $dateOk.Size = [Drawing.Size]::new(88, 32)
        $dateOk.DialogResult = [Windows.Forms.DialogResult]::OK
        $dateForm.Controls.Add($dateOk)
        $dateForm.AcceptButton = $dateOk
        $dateForm.CancelButton = $dateCancel

        try {
            if ($dateForm.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) {
                $Row.Cells['SentDate'].Value = $picker.Value.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
            }
        }
        finally {
            $dateForm.Dispose()
        }
        & $refreshRunState
    }

    $applyMappingsToGrid = {
        param($NewMappings)

        $script:activeMappings = $NewMappings
        $campaignTypeColumn.Items.Clear()
        foreach ($campaignItem in $script:activeMappings.CampaignTypes) {
            [void]$campaignTypeColumn.Items.Add([string]$campaignItem.CampaignType)
        }
        $salespersonColumn.Items.Clear()
        foreach ($salespersonItem in $script:activeMappings.Salespeople) {
            [void]$salespersonColumn.Items.Add([string]$salespersonItem.DisplayName)
        }
        foreach ($row in $grid.Rows) {
            if (-not $campaignTypeColumn.Items.Contains([string]$row.Cells['CampaignType'].Value)) {
                $row.Cells['CampaignType'].Value = $null
            }
            if (-not $salespersonColumn.Items.Contains([string]$row.Cells['Salesperson'].Value)) {
                $row.Cells['Salesperson'].Value = $null
            }
        }
        $catalogLabel.Text = 'Dropdown catalog: {0} | {1} campaign(s) | {2} salesperson mapping(s)' -f $script:activeMappings.SourceKind, $script:activeMappings.CampaignTypes.Count, $script:activeMappings.Salespeople.Count
        & $refreshRunState
    }

    $addButton.Add_Click({
        [void](& $addBlankRow)
        & $refreshRunState
    })

    $grid.Add_CellContentClick({
        param($sender, $eventArgs)
        if ($eventArgs.RowIndex -lt 0) {
            return
        }
        $row = $grid.Rows[$eventArgs.RowIndex]
        $columnName = $grid.Columns[$eventArgs.ColumnIndex].Name
        if ($columnName -eq 'File') {
            $dialog = [Windows.Forms.OpenFileDialog]::new()
            $dialog.Title = 'Choose the raw Mailchimp CSV file'
            $dialog.Filter = 'CSV files (*.csv)|*.csv'
            $dialog.Multiselect = $false
            $dialog.CheckFileExists = $true
            try {
                if ($dialog.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) {
                    & $loadFileIntoRow $row $dialog.FileName
                }
            }
            finally {
                $dialog.Dispose()
            }
        }
        elseif ($columnName -eq 'SentDate') {
            & $chooseDateForRow $row
        }
        elseif ($columnName -eq 'Remove') {
            $grid.Rows.RemoveAt($eventArgs.RowIndex)
            if ($grid.Rows.Count -eq 0) {
                [void](& $addBlankRow)
            }
            & $refreshRunState
        }
    })

    $grid.Add_CellValueChanged({ & $refreshRunState })
    $grid.Add_CurrentCellDirtyStateChanged({
        if ($grid.IsCurrentCellDirty) {
            [void]$grid.CommitEdit([Windows.Forms.DataGridViewDataErrorContexts]::Commit)
        }
    })
    $grid.Add_DataError({ param($sender, $eventArgs); $eventArgs.ThrowException = $false })

    $manageListsButton.Add_Click({
        try {
            $updatedMappings = Show-MappingEditor -Mappings $script:activeMappings
            if ($null -ne $updatedMappings) {
                & $applyMappingsToGrid $updatedMappings
                [Windows.Forms.MessageBox]::Show(
                    $form,
                    'The dropdown lists were saved and reloaded.',
                    'Dropdown lists updated',
                    [Windows.Forms.MessageBoxButtons]::OK,
                    [Windows.Forms.MessageBoxIcon]::Information) | Out-Null
            }
        }
        catch {
            Show-AppError $_.Exception.Message 'Dropdown lists could not be opened'
        }
    })

    $settingsButton.Add_Click({ [void](Show-CredentialDialog) })

    $runButton.Add_Click({
        if (-not $runButton.Enabled) {
            return
        }
        [void]$grid.EndEdit()

        $selections = [Collections.Generic.List[object]]::new()
        $summaryLines = [Collections.Generic.List[string]]::new()
        $number = 0
        foreach ($row in $grid.Rows) {
            $number++
            $campaignMatches = @($script:activeMappings.CampaignTypes | Where-Object CampaignType -eq ([string]$row.Cells['CampaignType'].Value))
            $salespersonMatches = @($script:activeMappings.Salespeople | Where-Object DisplayName -eq ([string]$row.Cells['Salesperson'].Value))
            if ($campaignMatches.Count -ne 1 -or $salespersonMatches.Count -ne 1) {
                Show-AppError "Row $number contains a dropdown value that is no longer available. Select it again." 'Queue could not be prepared'
                return
            }
            $sentDate = [datetime]::ParseExact(
                [string]$row.Cells['SentDate'].Value,
                'yyyy-MM-dd',
                [Globalization.CultureInfo]::InvariantCulture)
            $selection = [pscustomobject]@{
                Review = $row.Tag.Review
                EventId = [int]$row.Cells['EventId'].Value
                CampaignTitle = ([string]$row.Cells['CampaignTitle'].Value).Trim()
                CampaignSentDate = $sentDate.Date
                Campaign = $campaignMatches[0]
                Salesperson = $salespersonMatches[0]
                QueueIndex = $number
                QueueCount = $grid.Rows.Count
            }
            [void]$selections.Add($selection)
            [void]$summaryLines.Add(('{0}. {1} | {2} | {3} | Event {4} | {5} | {6} | {7:N0} engaged / {8:N0} clicked' -f
                $number,
                [IO.Path]::GetFileName($selection.Review.Path),
                $selection.CampaignTitle,
                $selection.CampaignSentDate.ToString('MMM d, yyyy'),
                $selection.EventId,
                [string]$selection.Campaign.CampaignType,
                [string]$selection.Salesperson.Salesperson,
                $selection.Review.EngagedCampaignRowCount,
                $selection.Review.EligibleRowCount))
        }

        $confirmText = @"
This will make LIVE changes in Momentus for $($selections.Count) queued file(s).

$($summaryLines -join [Environment]::NewLine)

Files run sequentially in the order shown. If one fails, later files will not start.

Continue with the live import queue?
"@
        $answer = [Windows.Forms.MessageBox]::Show(
            $form,
            $confirmText,
            'Confirm live Momentus import queue',
            [Windows.Forms.MessageBoxButtons]::YesNo,
            [Windows.Forms.MessageBoxIcon]::Warning)
        if ($answer -ne [Windows.Forms.DialogResult]::Yes) {
            return
        }

        $script:batchDialogSelections = @($selections)
        $form.DialogResult = [Windows.Forms.DialogResult]::OK
        $form.Close()
    })

    $firstRow = & $addBlankRow
    if (-not [string]::IsNullOrWhiteSpace($InitialFile)) {
        $form.Add_Shown({ & $loadFileIntoRow $firstRow $InitialFile })
    }
    & $refreshRunState

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:batchDialogSelections
}

function ConvertTo-CsvField {
    param([AllowNull()]$Value)

    $text = if ($null -eq $Value) { '' } else { [string]$Value }
    if ($text.Contains(',') -or $text.Contains('"') -or $text.Contains("`r") -or $text.Contains("`n")) {
        return '"' + $text.Replace('"', '""') + '"'
    }
    return $text
}

function Write-PreparedImportCsv {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Selection
    )

    $encoding = [Text.UTF8Encoding]::new($true)
    $writer = [IO.StreamWriter]::new($Path, $false, $encoding)
    try {
        $header = @(
            'Event ID',
            'Campaign',
            'Salesperson',
            'Salesperson Account Code',
            'Click Type',
            'Open Type',
            'Email',
            'Clicks',
            'Opens',
            'First Name',
            'Last Name',
            'Contact Account Code',
            'Campaign Sent Date',
            'Campaign Emails Sent',
            'Campaign Response Percentage')
        $writer.WriteLine((@($header | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))

        foreach ($row in $Selection.Review.EngagedCampaignRows) {
            $values = @(
                $Selection.EventId,
                [string]$Selection.CampaignTitle,
                [string]$Selection.Salesperson.Salesperson,
                [string]$Selection.Salesperson.SalespersonAccountCode,
                [string]$Selection.Campaign.ClickType,
                [string]$Selection.Campaign.OpenType,
                $row.Email,
                $row.Clicks,
                $row.Opens,
                $row.FirstName,
                $row.LastName,
                $row.ContactAccountCode,
                $Selection.CampaignSentDate.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture),
                $Selection.Review.TotalRows,
                $Selection.Review.ResponsePercentage)
            $writer.WriteLine((@($values | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))
        }
    }
    finally {
        $writer.Dispose()
    }
}

function Write-PreflightCsv {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Review
    )

    $encoding = [Text.UTF8Encoding]::new($true)
    $writer = [IO.StreamWriter]::new($Path, $false, $encoding)
    try {
        $writer.WriteLine('SourceRow,Email,FirstName,LastName,Opens,Clicks,ContactAccountCode,CampaignDetailStatus,ImportScope')
        foreach ($row in $Review.CampaignRows) {
            $scope = if ($row.Clicks -gt 0) {
                'ACTIVITY_NOTE_AND_CAMPAIGN'
            }
            elseif ($row.Opens -gt 0) {
                'CAMPAIGN_ONLY'
            }
            else {
                'IGNORED_NO_INTERACTION'
            }
            $values = @($row.SourceRow, $row.Email, $row.FirstName, $row.LastName, $row.Opens, $row.Clicks, $row.ContactAccountCode, $row.CampaignDetailStatus, $scope)
            $writer.WriteLine((@($values | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))
        }
        foreach ($row in $Review.RowsMissingAccount) {
            $values = @($row.SourceRow, $row.Email, $row.FirstName, $row.LastName, $row.Opens, $row.Clicks, '', $row.CampaignDetailStatus, 'SKIPPED_MISSING_ACCOUNT_CODE')
            $writer.WriteLine((@($values | ForEach-Object { ConvertTo-CsvField $_ }) -join ','))
        }
    }
    finally {
        $writer.Dispose()
    }
}

function Find-SyncCommand {
    $packagedExe = Join-Path $PSScriptRoot 'app\MailchimpMomentusSync.exe'
    if (Test-Path -LiteralPath $packagedExe -PathType Leaf) {
        return [pscustomobject]@{ Kind = 'Exe'; Path = $packagedExe }
    }

    $localBuildExe = Join-Path $PSScriptRoot 'artifacts\MailchimpMomentusSync\win-x64\MailchimpMomentusSync.exe'
    if (Test-Path -LiteralPath $localBuildExe -PathType Leaf) {
        return [pscustomobject]@{ Kind = 'Exe'; Path = $localBuildExe }
    }

    $automationRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $legacyRepoExe = Join-Path $automationRoot 'artifacts\MailchimpMomentusSync\win-x64\MailchimpMomentusSync.exe'
    if (Test-Path -LiteralPath $legacyRepoExe -PathType Leaf) {
        return [pscustomobject]@{ Kind = 'Exe'; Path = $legacyRepoExe }
    }

    $projectPath = Join-Path $PSScriptRoot 'MailchimpMomentusSync.csproj'
    if ((Test-Path -LiteralPath $projectPath -PathType Leaf) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Kind = 'Dotnet'; Path = $projectPath }
    }

    throw 'The Momentus sync engine is missing. Copy the complete package or run Build-Release.ps1 on a Windows computer with the .NET SDK.'
}

function Show-ProgressWindow {
    param([Parameter(Mandatory)][string]$RunFolder)

    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Mailchimp to Momentus Sync'
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $form.ControlBox = $false
    $form.ClientSize = [Drawing.Size]::new(660, 390)
    $form.BackColor = [Drawing.Color]::White
    $form.Font = [Drawing.Font]::new('Segoe UI', 9)

    $header = [Windows.Forms.Panel]::new()
    $header.Dock = [Windows.Forms.DockStyle]::Top
    $header.Height = 58
    $header.BackColor = $script:Navy
    $form.Controls.Add($header)

    $title = [Windows.Forms.Label]::new()
    $title.Text = 'IMPORT IN PROGRESS'
    $title.ForeColor = [Drawing.Color]::White
    $title.Font = [Drawing.Font]::new('Segoe UI', 13, [Drawing.FontStyle]::Bold)
    $title.AutoSize = $true
    $title.Location = [Drawing.Point]::new(20, 17)
    $header.Controls.Add($title)

    $statusLabel = [Windows.Forms.Label]::new()
    $statusLabel.Name = 'ProgressStatus'
    $statusLabel.Text = 'Starting the Momentus import...'
    $statusLabel.Font = [Drawing.Font]::new('Segoe UI', 10, [Drawing.FontStyle]::Bold)
    $statusLabel.AutoSize = $false
    $statusLabel.Size = [Drawing.Size]::new(610, 42)
    $statusLabel.Location = [Drawing.Point]::new(22, 74)
    $form.Controls.Add($statusLabel)

    $progressBar = [Windows.Forms.ProgressBar]::new()
    $progressBar.Style = [Windows.Forms.ProgressBarStyle]::Marquee
    $progressBar.MarqueeAnimationSpeed = 25
    $progressBar.Location = [Drawing.Point]::new(22, 121)
    $progressBar.Size = [Drawing.Size]::new(610, 22)
    $form.Controls.Add($progressBar)

    $progressDetails = [Windows.Forms.TextBox]::new()
    $progressDetails.Name = 'ProgressDetails'
    $progressDetails.Multiline = $true
    $progressDetails.ReadOnly = $true
    $progressDetails.ScrollBars = [Windows.Forms.ScrollBars]::Vertical
    $progressDetails.WordWrap = $false
    $progressDetails.BackColor = [Drawing.Color]::FromArgb(246, 248, 251)
    $progressDetails.Font = [Drawing.Font]::new('Consolas', 8.5)
    $progressDetails.Location = [Drawing.Point]::new(22, 158)
    $progressDetails.Size = [Drawing.Size]::new(610, 155)
    $progressDetails.Text = 'Waiting for the sync engine to report progress...'
    $form.Controls.Add($progressDetails)

    $detail = [Windows.Forms.Label]::new()
    $detail.Text = 'Run folder: ' + $RunFolder
    $detail.ForeColor = $script:Muted
    $detail.AutoEllipsis = $true
    $detail.AutoSize = $false
    $detail.Size = [Drawing.Size]::new(610, 42)
    $detail.Location = [Drawing.Point]::new(22, 329)
    $form.Controls.Add($detail)

    $form.Tag = [pscustomobject]@{
        StatusLabel = $statusLabel
        DetailsBox = $progressDetails
    }

    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    return $form
}

function Get-EngineOutputSummary {
    param([Parameter(Mandatory)][string]$OutputLog)

    $text = ''
    if (Test-Path -LiteralPath $OutputLog -PathType Leaf) {
        $text = Get-Content -LiteralPath $OutputLog -Raw -ErrorAction SilentlyContinue
    }

    $successCount = $null
    $errorCount = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        $successMatch = [Text.RegularExpressions.Regex]::Match(
            $text,
            '(?im)^\s*Files processed successfully:\s*(\d+)\s*$')
        $errorMatch = [Text.RegularExpressions.Regex]::Match(
            $text,
            '(?im)^\s*Files with errors:\s*(\d+)\s*$')

        if ($successMatch.Success) {
            $successCount = [int]$successMatch.Groups[1].Value
        }
        if ($errorMatch.Success) {
            $errorCount = [int]$errorMatch.Groups[1].Value
        }
    }

    return [pscustomobject]@{
        FilesProcessedSuccessfully = $successCount
        FilesWithErrors = $errorCount
        ConfirmedSuccess = ($null -ne $successCount -and $successCount -gt 0 -and $errorCount -eq 0)
    }
}

function Invoke-SyncEngine {
    param(
        [Parameter(Mandatory)]$Command,
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)]$ProgressForm
    )

    $stdoutLog = Join-Path $RunRoot 'sync-output.log'
    $stderrLog = Join-Path $RunRoot 'sync-errors.log'

    if ($Command.Kind -eq 'Exe') {
        $startInfo = @{
            FilePath = $Command.Path
            ArgumentList = @('--apply')
            WorkingDirectory = $RunRoot
            WindowStyle = 'Hidden'
            RedirectStandardOutput = $stdoutLog
            RedirectStandardError = $stderrLog
            PassThru = $true
        }
        $process = Start-Process @startInfo
    }
    else {
        $quotedProjectPath = '"' + $Command.Path + '"'
        $arguments = @('run', '--project', $quotedProjectPath, '--configuration', 'Release', '--', '--apply')
        $startInfo = @{
            FilePath = 'dotnet.exe'
            ArgumentList = $arguments
            WorkingDirectory = $RunRoot
            WindowStyle = 'Hidden'
            RedirectStandardOutput = $stdoutLog
            RedirectStandardError = $stderrLog
            PassThru = $true
        }
        $process = Start-Process @startInfo
    }

    $lastProgressText = ''
    $updateProgress = {
        if (-not (Test-Path -LiteralPath $stdoutLog -PathType Leaf)) {
            return
        }

        $lines = @(Get-Content -LiteralPath $stdoutLog -Tail 14 -ErrorAction SilentlyContinue |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_) -and
                [string]$_ -notmatch '^=+$'
            })
        if ($lines.Count -eq 0) {
            return
        }

        $newProgressText = $lines -join [Environment]::NewLine
        if ($newProgressText -eq $lastProgressText) {
            return
        }

        $lastProgressText = $newProgressText
        if ($null -ne $ProgressForm.Tag) {
            $ProgressForm.Tag.DetailsBox.Text = $newProgressText
            $ProgressForm.Tag.DetailsBox.SelectionStart = $ProgressForm.Tag.DetailsBox.TextLength
            $ProgressForm.Tag.DetailsBox.ScrollToCaret()
            $ProgressForm.Tag.StatusLabel.Text = [string]$lines[-1]
        }
    }

    while (-not $process.HasExited) {
        & $updateProgress
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 300
    }

    $process.WaitForExit()
    $process.Refresh()
    & $updateProgress
    [Windows.Forms.Application]::DoEvents()

    $exitCode = $null
    try {
        $rawExitCode = $process.ExitCode
        if ($null -ne $rawExitCode -and -not [string]::IsNullOrWhiteSpace([string]$rawExitCode)) {
            $exitCode = [int]$rawExitCode
        }
    }
    catch {
        $exitCode = $null
    }

    $summary = Get-EngineOutputSummary -OutputLog $stdoutLog
    $succeeded = $false
    $completionSource = 'No success result available'
    if ($null -ne $exitCode) {
        $succeeded = ($exitCode -eq 0)
        $completionSource = 'Process exit code'
    }
    elseif ($summary.ConfirmedSuccess) {
        $succeeded = $true
        $completionSource = 'Engine output summary'
    }

    $result = [pscustomobject]@{
        Succeeded = $succeeded
        ExitCode = $exitCode
        CompletionSource = $completionSource
        FilesProcessedSuccessfully = $summary.FilesProcessedSuccessfully
        FilesWithErrors = $summary.FilesWithErrors
        StandardOutputLog = $stdoutLog
        StandardErrorLog = $stderrLog
    }

    $process.Dispose()
    return $result
}

function Invoke-ImportWorkflow {
    param(
        [Parameter(Mandatory)]$Selection,
        [Parameter(Mandatory)]$Mappings,
        [switch]$SuppressResultUi
    )

    $credential = Get-SavedCredential
    if ($null -eq $credential) {
        throw 'Momentus API credentials have not been saved.'
    }

    $runId = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $runRoot = Join-Path $script:AppRoot ('runs\' + $runId)
    $sourcePath = Join-Path $runRoot 'source'
    $pendingPath = Join-Path $runRoot 'pending'
    $completePath = Join-Path $runRoot 'complete'
    New-Item -ItemType Directory -Path $sourcePath -Force | Out-Null
    New-Item -ItemType Directory -Path $pendingPath -Force | Out-Null
    New-Item -ItemType Directory -Path $completePath -Force | Out-Null

    $sourceCopy = Join-Path $sourcePath ([IO.Path]::GetFileName($Selection.Review.Path))
    Copy-Item -LiteralPath $Selection.Review.Path -Destination $sourceCopy -Force

    $preparedName = [IO.Path]::GetFileNameWithoutExtension($Selection.Review.Path) + '_prepared_for_momentus.csv'
    $preparedPath = Join-Path $pendingPath $preparedName
    Write-PreparedImportCsv -Path $preparedPath -Selection $Selection
    Write-PreflightCsv -Path (Join-Path $runRoot 'mailchimp_preflight.csv') -Review $Selection.Review

    [pscustomobject]@{
        RunId = $runId
        CreatedAt = (Get-Date).ToUniversalTime().ToString('o')
        CatalogVersion = [string]$Mappings.Version
        CatalogSource = [string]$Mappings.SourceKind
        CatalogPath = [string]$Mappings.Path
        SourceFile = $Selection.Review.Path
        SourceCopy = $sourceCopy
        PreparedImportFile = $preparedPath
        EventId = $Selection.EventId
        Campaign = [string]$Selection.CampaignTitle
        CampaignSentDate = $Selection.CampaignSentDate.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        CampaignType = [string]$Selection.Campaign.CampaignType
        ClickType = [string]$Selection.Campaign.ClickType
        OpenType = [string]$Selection.Campaign.OpenType
        Salesperson = [string]$Selection.Salesperson.Salesperson
        SalespersonAccountCode = [string]$Selection.Salesperson.SalespersonAccountCode
        TotalSourceRows = $Selection.Review.TotalRows
        ClickedSourceRows = $Selection.Review.ClickedRows
        ClickQualifiedRows = $Selection.Review.EligibleRowCount
        CampaignRecipientRows = $Selection.Review.EngagedCampaignRowCount
        CampaignIgnoredNoInteraction = $Selection.Review.NoInteractionCount
        CampaignOpened = $Selection.Review.OpenedCount
        CampaignOpenedAndClicked = $Selection.Review.OpenedClickedCount
        EmailsSent = $Selection.Review.TotalRows
        CampaignResponsePercentage = $Selection.Review.ResponsePercentage
        SkippedNoClick = $Selection.Review.SkippedNoClick
        SkippedMissingAccount = $Selection.Review.SkippedMissingAccount
        QueueIndex = if ($null -ne $Selection.PSObject.Properties['QueueIndex']) { $Selection.QueueIndex } else { 1 }
        QueueCount = if ($null -ne $Selection.PSObject.Properties['QueueCount']) { $Selection.QueueCount } else { 1 }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'run-settings.json') -Encoding UTF8

    $command = Find-SyncCommand
    $progressForm = Show-ProgressWindow -RunFolder $runRoot
    if ($null -ne $Selection.PSObject.Properties['QueueIndex']) {
        $progressForm.Text = 'Mailchimp to Momentus Sync - File {0} of {1}' -f $Selection.QueueIndex, $Selection.QueueCount
        if ($null -ne $progressForm.Tag) {
            $progressForm.Tag.StatusLabel.Text = 'Starting file {0} of {1}: {2}' -f $Selection.QueueIndex, $Selection.QueueCount, $Selection.CampaignTitle
        }
    }

    $env:MOMENTUS_APIUSER = [string]$credential.ApiUser
    $env:MOMENTUS_SECRET = Convert-SecureStringToPlainText $credential.Secret
    $env:MOMENTUS_KEY = Convert-SecureStringToPlainText $credential.Key

    try {
        $result = Invoke-SyncEngine -Command $command -RunRoot $runRoot -ProgressForm $progressForm
    }
    finally {
        Remove-Item Env:MOMENTUS_APIUSER -ErrorAction SilentlyContinue
        Remove-Item Env:MOMENTUS_SECRET -ErrorAction SilentlyContinue
        Remove-Item Env:MOMENTUS_KEY -ErrorAction SilentlyContinue
        $credential = $null
        if ($null -ne $progressForm -and -not $progressForm.IsDisposed) {
            $progressForm.Close()
            $progressForm.Dispose()
        }
    }

    [pscustomobject]@{
        Succeeded = $result.Succeeded
        ExitCode = $result.ExitCode
        CompletionSource = $result.CompletionSource
        FilesProcessedSuccessfully = $result.FilesProcessedSuccessfully
        FilesWithErrors = $result.FilesWithErrors
        RecordedAt = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'launcher-result.json') -Encoding UTF8

    if ($result.Succeeded) {
        $message = @"
The Momentus sync completed.

Event: $($Selection.EventId)
Campaign: $([string]$Selection.CampaignTitle)
Campaign Sent Date: $($Selection.CampaignSentDate.ToString('MMMM d, yyyy'))
Campaign Type: $([string]$Selection.Campaign.CampaignType)
Clicked records submitted: $($Selection.Review.EligibleRowCount)
Engaged campaign contacts submitted: $($Selection.Review.EngagedCampaignRowCount)

Results and audit folder:
$completePath
"@
        if (-not $SuppressResultUi) {
            [Windows.Forms.MessageBox]::Show(
                $message,
                'Sync completed',
                [Windows.Forms.MessageBoxButtons]::OK,
                [Windows.Forms.MessageBoxIcon]::Information) | Out-Null
            Start-Process explorer.exe -ArgumentList ('"' + $completePath + '"')
        }
        return [pscustomobject]@{
            Succeeded = $true
            Campaign = [string]$Selection.CampaignTitle
            RunRoot = $runRoot
            CompletePath = $completePath
            Result = $result
            FailureMessage = ''
            StoppedBeforeWrites = $false
        }
    }

    $errorTail = ''
    foreach ($logPath in @($result.StandardErrorLog, $result.StandardOutputLog)) {
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            $tail = @(Get-Content -LiteralPath $logPath -Tail 12 -ErrorAction SilentlyContinue)
            if ($tail.Count -gt 0) {
                $errorTail += ($tail -join [Environment]::NewLine) + [Environment]::NewLine
            }
        }
    }

    $exitCodeDescription = if ($null -eq $result.ExitCode) {
        'not reported by Windows'
    }
    else {
        [string]$result.ExitCode
    }

    $stoppedBeforeWrites = $false
    foreach ($logPath in @($result.StandardErrorLog, $result.StandardOutputLog)) {
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            $logText = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
            if ($logText -like '*No live changes were made for this file.*') {
                $stoppedBeforeWrites = $true
                break
            }
        }
    }

    if ($stoppedBeforeWrites) {
        $failureMessage = @"
The sync stopped during its safety checks before any Momentus records were changed (exit code: $exitCodeDescription).

It is safe to rerun after the reported issue is corrected or the application is updated.

Run folder:
$runRoot
"@
    }
    else {
        $failureMessage = @"
The sync did not finish successfully (exit code: $exitCodeDescription).

Do not rerun until the audit and log files are reviewed. Some Momentus records may already have been created before the error.

Run folder:
$runRoot
"@
    }
    if (-not [string]::IsNullOrWhiteSpace($errorTail)) {
        $failureMessage += [Environment]::NewLine + 'Latest log details:' + [Environment]::NewLine + $errorTail.Trim()
    }

    if (-not $SuppressResultUi) {
        [Windows.Forms.MessageBox]::Show(
            $failureMessage,
            'Sync needs attention',
            [Windows.Forms.MessageBoxButtons]::OK,
            [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        Start-Process explorer.exe -ArgumentList ('"' + $runRoot + '"')
    }
    return [pscustomobject]@{
        Succeeded = $false
        Campaign = [string]$Selection.CampaignTitle
        RunRoot = $runRoot
        CompletePath = $completePath
        Result = $result
        FailureMessage = $failureMessage
        StoppedBeforeWrites = $stoppedBeforeWrites
    }
}

try {
    New-Item -ItemType Directory -Path $script:AppRoot -Force | Out-Null

    if ($ConfigureCredentialsOnly) {
        [void](Show-CredentialDialog -RequireCredentials)
        exit 0
    }

    if ($ConfigureMappingsOnly) {
        $currentMappings = Get-ImportMappings
        [void](Show-MappingEditor -Mappings $currentMappings)
        exit 0
    }

    $mappings = Get-ImportMappings

    if (-not (Test-Path -LiteralPath $script:CredentialPath -PathType Leaf)) {
        $saved = Show-CredentialDialog -RequireCredentials
        if (-not $saved -and -not (Test-Path -LiteralPath $script:CredentialPath -PathType Leaf)) {
            exit 0
        }
    }
    else {
        [void](Get-SavedCredential)
    }

    $selections = @(Select-ImportQueueOptions -Mappings $mappings -InitialFile $InputFile)
    if ($selections.Count -eq 0) {
        exit 0
    }

    $mappings = Get-ImportMappings
    $batchResults = [Collections.Generic.List[object]]::new()
    foreach ($selection in $selections) {
        $workflowResult = Invoke-ImportWorkflow -Selection $selection -Mappings $mappings -SuppressResultUi
        [void]$batchResults.Add($workflowResult)
        if (-not $workflowResult.Succeeded) {
            break
        }
    }

    $successfulResults = @($batchResults | Where-Object Succeeded)
    $failedResult = @($batchResults | Where-Object { -not $_.Succeeded } | Select-Object -First 1)
    if ($failedResult.Count -eq 0 -and $successfulResults.Count -eq $selections.Count) {
        $completedLines = @($successfulResults | ForEach-Object {
            '- {0}: {1}' -f $_.Campaign, $_.CompletePath
        })
        $batchMessage = @"
The Momentus import queue completed successfully.

Files completed: $($successfulResults.Count)

$($completedLines -join [Environment]::NewLine)
"@
        [Windows.Forms.MessageBox]::Show(
            $batchMessage,
            'Import queue completed',
            [Windows.Forms.MessageBoxButtons]::OK,
            [Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    }
    else {
        $notStarted = $selections.Count - $batchResults.Count
        $batchMessage = $failedResult[0].FailureMessage + [Environment]::NewLine + [Environment]::NewLine +
            ('Queue summary: {0} completed, 1 failed, {1} not started.' -f $successfulResults.Count, $notStarted)
        [Windows.Forms.MessageBox]::Show(
            $batchMessage,
            'Import queue stopped',
            [Windows.Forms.MessageBoxButtons]::OK,
            [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    Start-Process explorer.exe -ArgumentList ('"' + (Join-Path $script:AppRoot 'runs') + '"')
    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($_.ScriptStackTrace) {
        $message += [Environment]::NewLine + [Environment]::NewLine + 'Technical location:' + [Environment]::NewLine + $_.ScriptStackTrace
    }
    Show-AppError $message 'Mailchimp to Momentus Sync'
    exit 1
}

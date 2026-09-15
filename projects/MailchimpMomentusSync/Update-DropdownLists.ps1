[CmdletBinding()]
param(
    [string]$TemplateFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Select-TemplateCsv {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = [Windows.Forms.OpenFileDialog]::new()
    $dialog.Title = 'Choose the existing 12-column Momentus import template'
    $dialog.Filter = 'CSV files (*.csv)|*.csv'
    $dialog.Multiselect = $false
    $dialog.CheckFileExists = $true

    try {
        if ($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) {
            return $null
        }
        return $dialog.FileName
    }
    finally {
        $dialog.Dispose()
    }
}

function Read-TemplateMappings {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName Microsoft.VisualBasic
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($Path)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true

    $campaignsByName = @{}
    $salespeopleByName = @{}

    try {
        if ($parser.EndOfData) {
            throw 'The template file is empty.'
        }

        $header = $parser.ReadFields()
        if ($null -eq $header -or $header.Count -lt 6) {
            throw 'The template must contain at least columns A through F.'
        }

        $rowNumber = 1
        while (-not $parser.EndOfData) {
            $rowNumber++
            $fields = $parser.ReadFields()
            if ($null -eq $fields -or [string]::IsNullOrWhiteSpace(($fields -join ''))) {
                continue
            }
            if ($fields.Count -lt 6) {
                throw "Template row $rowNumber has fewer than six columns."
            }

            $campaignType = ([string]$fields[1]).Trim()
            $salesperson = ([string]$fields[2]).Trim()
            $salespersonAccountCode = ([string]$fields[3]).Trim()
            $clickType = ([string]$fields[4]).Trim()
            $openType = ([string]$fields[5]).Trim()

            if (-not [string]::IsNullOrWhiteSpace($campaignType) -and
                -not [string]::IsNullOrWhiteSpace($clickType) -and
                -not [string]::IsNullOrWhiteSpace($openType)) {
                $campaignKey = $campaignType.ToUpperInvariant()
                if ($campaignsByName.ContainsKey($campaignKey)) {
                    $existing = $campaignsByName[$campaignKey]
                    if (-not [string]::Equals($existing.ClickType, $clickType, [StringComparison]::OrdinalIgnoreCase) -or
                        -not [string]::Equals($existing.OpenType, $openType, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Campaign type '$campaignType' has conflicting click/open type codes in the template."
                    }
                }
                else {
                    $campaignsByName[$campaignKey] = [pscustomobject]@{
                        CampaignType = $campaignType
                        ClickType = $clickType
                        OpenType = $openType
                    }
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($salesperson) -and
                -not [string]::IsNullOrWhiteSpace($salespersonAccountCode)) {
                $salespersonKey = $salesperson.ToUpperInvariant()
                if ($salespeopleByName.ContainsKey($salespersonKey)) {
                    $existing = $salespeopleByName[$salespersonKey]
                    if (-not [string]::Equals(
                        $existing.SalespersonAccountCode,
                        $salespersonAccountCode,
                        [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Salesperson '$salesperson' has conflicting account codes in the template."
                    }
                }
                else {
                    $salespeopleByName[$salespersonKey] = [pscustomobject]@{
                        Salesperson = $salesperson
                        SalespersonAccountCode = $salespersonAccountCode
                    }
                }
            }
        }
    }
    finally {
        $parser.Close()
    }

    $campaigns = @($campaignsByName.Values | Sort-Object CampaignType)
    $salespeople = @($salespeopleByName.Values | Sort-Object Salesperson)

    if ($campaigns.Count -eq 0) {
        throw 'No complete campaign mappings were found in columns B, E, and F.'
    }
    if ($salespeople.Count -eq 0) {
        throw 'No complete salesperson mappings were found in columns C and D.'
    }

    return [pscustomobject]@{
        CampaignTypes = $campaigns
        Salespeople = $salespeople
    }
}

if ([string]::IsNullOrWhiteSpace($TemplateFile)) {
    $TemplateFile = Select-TemplateCsv
}

if ([string]::IsNullOrWhiteSpace($TemplateFile)) {
    Write-Host 'No template was selected. Dropdown lists were not changed.'
    exit 0
}

$resolvedTemplate = (Resolve-Path -LiteralPath $TemplateFile).Path
$mappings = Read-TemplateMappings -Path $resolvedTemplate
$appRoot = Join-Path $env:LOCALAPPDATA 'Kallman\MailchimpMomentusSync'
$mappingPath = Join-Path $appRoot 'ImportMappings.json'
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null

[pscustomobject]@{
    Salespeople = $mappings.Salespeople
    CampaignTypes = $mappings.CampaignTypes
    SourceTemplate = $resolvedTemplate
    UpdatedAt = (Get-Date).ToString('o')
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $mappingPath -Encoding UTF8

Write-Host ''
Write-Host 'Dropdown lists updated.'
Write-Host ('Campaign types loaded: ' + $mappings.CampaignTypes.Count)
Write-Host ('Salespeople loaded: ' + $mappings.Salespeople.Count)
Write-Host ('Saved to: ' + $mappingPath)

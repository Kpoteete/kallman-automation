namespace AccountImport.Models;

public static class ColumnMap
{
    // Template v10g layout. The main mapping code also searches by Row 2 friendly headers,
    // so minor future column movement is tolerated.
    public const int CompanyName = 1;          // A - Company Name
    public const int AccountCode = 26;         // Z - Account Code (if account already exists)
    public const int MarketSegmentMajor = 27;  // AA - Company Market Segment major Code
    public const int Country = 33;             // AG - Country Code
    public const int ContactEmail = 19;        // S - Contact Email
    public const int DuplicateFlag = 25;       // Y - Email Exists / duplicate flag

    // Output columns start after the current template's last helper column AN.
    public const int AccountMatchFound = 41;   // AO
    public const int AccountCodeUsed = 42;     // AP
    public const int ImportStatus = 43;        // AQ
    public const int ImportMessage = 44;       // AR
    public const int SourceRowNumber = 45;     // AS
    public const int SourceFileName = 46;      // AT

    public const int HeaderApiRow = 1;
    public const int HeaderFriendlyRow = 2;
    public const int FirstDataRow = 3;
}

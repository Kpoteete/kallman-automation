\# Automated Service Order Entry



\## Goal



Finish service order entry for orders submitted through the exhibitor portal.



\## Summary



When a service order is submitted through the Exhibitor Portal, several fields still need to be completed before the order can be sent to Finance for invoicing.



The automation will:



1\. Find newly submitted portal orders.

2\. Gather the required information from the Exhibitor, Service Order, Account, Contacts, and lookup files.

3\. Store the proposed updates in a CSV or Excel run file.

4\. Validate that the required information was found.

5\. Update the Service Order in Momentus.

6\. Leave orders unchanged when required information cannot be determined safely.

7\. Prepare completed orders for Finance/invoicing.



\---



\# Order Identification



An order should be processed when both of the following are true:



\- Exhibitor Status = \*\*Online Booth Order\*\*

&#x20; - Sequence Number: `35`

\- Service Order Status = \*\*Pending Completion\*\*

&#x20; - Code: `PC`



\---



\# Fields to Complete



The automation needs to complete:



\- Order Category

\- Salesperson / Order Account Rep

\- Order Booth Number

\- Bill-To Account

\- Bill-To Contact

\- Bill-To Address, when applicable



\---



\# Run File



For every order found during the run, create one row containing the information below.



\## Order Information



| Column | Field |

|---|---|

| A | EventID |

| B | ExhibitorID |

| C | Order Number |

| D | Order Date |

| E | Sales Rep from Exhibitor Record |

| F | OrderAccountRep value to enter on Service Order |

| G | Service Order Item Identifier |

| H | Service Order Category to Apply |



\## Existing Service Order Bill-To Information



These fields represent what is already on the Service Order before the automation makes any changes.



| Column | Field |

|---|---|

| I | Existing BillToAccount Code / ID |

| J | Existing BillToAccount Company Name |

| K | Existing BillToAccount Address |

| L | Existing BillToAccount City |

| M | Existing BillToAccount State |

| N | Existing BillToAccount Postal Code |

| O | Existing BillToAccount Country |

| P | Existing BillToContact Code / ID |

| Q | Existing BillToContact First Name |

| R | Existing BillToContact Last Name |

| S | Existing BillToContact Email |



\## Requested Billing Information from Account UDFs



| Column | Field | Account UDF |

|---|---|---|

| T | Requested Bill-To Company Name | `TXT\_03` |

| U | Invoice Attention Of | `TXT\_13` |

| V | Requested Bill-To Contact First Name | `TXT\_05` |

| W | Requested Bill-To Contact Last Name | `TXT\_09` |

| X | Requested Bill-To Contact Email | `TXT\_10` |

| Y | Send Invoice to Following Address | `TXT\_11` |

| Z | Requested Bill-To Address | `TXT\_04` |

| AA | Requested Bill-To City | `TXT\_15` |

| AB | Requested Bill-To State | `TXT\_06` |

| AC | Requested Bill-To Postal Code | `TXT\_07` |

| AD | Requested Bill-To Country | `TXT\_08` |



\## Automation Decision Fields



These columns show what the automation decided to do.



| Column | Field |

|---|---|

| AE | Bill-To Address Action |

| AF | Bill-To Contact Match Result |

| AG | Matching Contact Code / ID |

| AH | Contact Action |

| AI | Final BillToAccount Code / ID |

| AJ | Final BillToContact Code / ID |

| AK | Final Bill-To Address |

| AL | Final Bill-To City |

| AM | Final Bill-To State |

| AN | Final Bill-To Postal Code |

| AO | Final Bill-To Country |

| AP | Booth Number to Apply |

| AQ | Validation Status |

| AR | Validation Message |

| AS | Service Order Update Status |

| AT | Update/Error Message |



\---



\# Entering the Salesperson



On the Exhibitor record there is a UDF containing the Sales Rep.



Exhibitor UDF:



`TXT\_02`



Inside the project folder there is an Excel file named:



`SalesRepLookup.xlsx`



The lookup file contains:



| Column | Description |

|---|---|

| A | Exhibitor Record UDF Sales Rep |

| B | UDF Code |

| C | Account Code to Enter on Service Order |



The value found in Exhibitor `TXT\_02` may match either Column A or Column B.



When a match is found, use the corresponding value from Column C.



Enter that value into:



`OrderAccountRep`



\### Fallback



If Exhibitor `TXT\_02` is blank, use:



`Salesperson`



from the Exhibitor record.



If a valid Service Order Account Rep still cannot be determined, do not update the Salesperson field and flag the order for review.



\---



\# Entering the Order Category



Order Category requires matching the Service Order Item description against a lookup table.



Inside the project folder there is an Excel file named:



`OrderCategoryLookup.xlsx`



The lookup contains:



| Column | Description |

|---|---|

| A | Order Category Name |

| B | Order Category Code |

| C | Comma-separated matching values |



\## Matching Logic



Look through the Service Order Items attached to the Service Order.



Identify Service Order Items containing the word:



`package`



The relevant identifier is generally the text appearing before the word `package`.



Compare the Service Order Item text against the comma-separated matching values in Column C of `OrderCategoryLookup.xlsx`.



Column C is the controlling matching rule.



If a value from Column C matches the Service Order Item, use the Order Category from that row.



Enter the corresponding Order Category Code from Column B into the Service Order.



\### Multiple Package Lines



A Service Order may contain multiple Service Order Items containing the word `package`.



Do not automatically use the first package line.



Check all applicable Service Order Items against the values in Column C.



A match against Column C determines the correct Order Category.



\### No Match



If no Order Category lookup match can be confidently determined:



\*\*Do not enter an Order Category.\*\*



Set:



`Validation Status = REVIEW`



and record the reason in:



`Validation Message`



\---



\# Entering the Booth Number



The booth-number logic has already been created in the existing project:



`C:\\kwi-automations\\momentus\\ServiceOrderBoothUpdater`



Move or reuse that logic inside the Automated Service Order Entry project.



The resulting booth number should be stored in:



`AP - Booth Number to Apply`



before updating the Service Order.



\---



\# Bill-To Information



Billing information entered by the customer during the online contract process is stored in Account UDFs.



| Description | UDF |

|---|---|

| Bill-To Company Name | `TXT\_03` |

| Invoice To The Attention Of | `TXT\_13` |

| Bill-To Contact First Name | `TXT\_05` |

| Bill-To Contact Last Name | `TXT\_09` |

| Bill-To Contact Email Address | `TXT\_10` |

| Send Invoice to the Following Address | `TXT\_11` |

| Bill-To Address | `TXT\_04` |

| Bill-To City | `TXT\_15` |

| Bill-To State | `TXT\_06` |

| Bill-To Postal Code | `TXT\_07` |

| Bill-To Country | `TXT\_08` |



The Service Order will normally already contain:



`BillToAccount`



and



`BillToContact`



The existing values should not automatically be replaced.



The automation must compare the existing Service Order billing information against the billing instructions entered by the customer.



\---



\# Bill-To Address Logic



First inspect:



`TXT\_11 - Send Invoice to the Following Address`



\## If TXT\_11 = No



Keep the existing Bill-To Account and existing Bill-To address information.



Set:



`Bill-To Address Action = KEEP EXISTING`



Do not replace the existing address using `TXT\_04`, `TXT\_15`, `TXT\_06`, `TXT\_07`, or `TXT\_08`.



\## If TXT\_11 = Yes



Use the billing address entered in the Account UDFs:



\- Address = `TXT\_04`

\- City = `TXT\_15`

\- State = `TXT\_06`

\- Postal Code = `TXT\_07`

\- Country = `TXT\_08`



Set:



`Bill-To Address Action = USE REQUESTED ADDRESS`



Before updating Momentus, verify that enough address information exists to create or apply the billing address safely.



If the required information is incomplete, do not make the address change and flag the order for review.



\---



\# Bill-To Contact Logic



The requested billing email is:



`TXT\_10`



Compare this email address to the email address of the Service Order's current:



`BillToContact`



Email comparisons should be case-insensitive and should ignore leading or trailing spaces.



\## Scenario 1 - Email Matches Existing BillToContact



If the email address from `TXT\_10` matches the current BillToContact email:



\- Keep the existing BillToContact.

\- Do not create another contact.



Set:



`Bill-To Contact Match Result = CURRENT CONTACT MATCH`



Set:



`Contact Action = KEEP EXISTING`



\---



\## Scenario 2 - Email Does Not Match Existing BillToContact



Search the Bill-To Account's contacts for a contact with the email address contained in:



`TXT\_10`



\### Existing Contact Found



If a contact exists with that email address:



\- Store its Contact Code / ID in Column AG.

\- Use that contact as the Service Order `BillToContact`.



Set:



`Bill-To Contact Match Result = ACCOUNT CONTACT MATCH`



Set:



`Contact Action = USE EXISTING CONTACT`



\### Existing Contact Not Found



Create a new Contact using:



\- First Name = `TXT\_05`

\- Last Name = `TXT\_09`

\- Email = `TXT\_10`



Associate the new Contact with the appropriate Account.



After the Contact is successfully created, use the newly created Contact as:



`BillToContact`



Set:



`Bill-To Contact Match Result = NO MATCH`



Set:



`Contact Action = CREATE CONTACT`



Store the new Contact Code / ID in Column AG.



\---



\# Bill-To Account Logic



The Service Order already contains:



`BillToAccount`



The automation should preserve this account unless the billing information clearly requires another account to be used.



The requested company name is stored in:



`TXT\_03`



For the first version of this automation, the requested company name should be stored and compared for auditing purposes.



The automation should \*\*not automatically create or change the BillToAccount solely because the company name in TXT\_03 is different\*\*.



If the company name differs materially from the existing BillToAccount company name, flag the order for review.



Set:



`Validation Status = REVIEW`



with a message similar to:



`Requested Bill-To company differs from existing BillToAccount.`



This prevents the automation from accidentally assigning an invoice to the wrong Account.



\---



\# Validation



Before updating the Service Order, validate the completed row.



\## Required Before Automatic Completion



The automation should have:



\- EventID

\- ExhibitorID

\- Order Number

\- Salesperson / OrderAccountRep

\- Order Category

\- Booth Number

\- BillToAccount

\- BillToContact

\- A valid billing address decision



If everything required is valid:



`Validation Status = READY`



If something cannot be determined safely:



`Validation Status = REVIEW`



The automation should not finish the Service Order when the status is `REVIEW`.



The reason should be stored in:



`Validation Message`



Examples:



\- `Order Category could not be determined`

\- `Sales Rep lookup failed`

\- `Requested billing email is blank`

\- `Requested billing address is incomplete`

\- `Requested Bill-To company differs from existing BillToAccount`

\- `Contact creation failed`

\- `Booth number could not be determined`



\---



\# Completing the Service Order



Once all fields have been gathered and the row has:



`Validation Status = READY`



update the Service Order with the determined values.



Fields include:



\- Order Category

\- `OrderAccountRep`

\- Booth Number

\- `BillToAccount`

\- `BillToContact`

\- Billing address information when applicable



After all updates succeed, set:



`Service Order Update Status = COMPLETED`



If any update fails:



`Service Order Update Status = FAILED`



and record the API or processing error in:



`Update/Error Message`



Do not mark an order as completed if only some of the required updates succeeded.



\---



\# Finance Handoff



After the Service Order has been successfully completed, it can move to the next stage for Finance to invoice.



The exact Service Order status or Finance handoff action should be defined separately before the automation changes the Service Order out of `Pending Completion`.



For the initial development version, the automation should complete the required Service Order fields but should not automatically move the order to the Finance/invoicing status until that final status and workflow have been confirmed.



## Step 4 authoritative billing rules (2026-10-04)

The revised user-approved Step 4 requirements supersede this original design's conflicting Bill-To instructions. Above Address (`Use Above Address`, `ECA`, `No`, `N`, blank) retains the existing order billing account/contact and ignores separate billing fields. It cannot create, change or reassign separate billing records. Explicit Below Address (`BA`, `Yes`, `Y`, `Below Address`) uses complete duplicate checks, confirmed identity reuse, or valid no-match account/contact creation. Ambiguity, incomplete requests and uncertain existing identities require REVIEW.

New organization accounts preserve the exact contract company Name and require a configured/verified Not Applicable Event Sales Account Status code. Existing names, addresses and Event Sales Status are preserved. Cross-account email evidence requires REVIEW. Explicit UDF Header/Class/Type configuration and current effective account/contact validation are required; collection order and populated fields do not control selector behavior. Pending creations are PREPARED until returned IDs/readback establish READY. See README for configuration and detailed current billing/recovery behavior. Later contract/note/category/handoff/activation remediation is outside this step.

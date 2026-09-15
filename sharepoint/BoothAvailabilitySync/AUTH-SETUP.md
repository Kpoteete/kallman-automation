# Local authentication setup

The code is ready to run, but `appsettings.json` intentionally does not contain a Microsoft Entra client ID yet.

For laptop testing, use an interactive browser sign-in. This requires a one-time Entra app registration.

## Create the app registration

1. Open **Microsoft Entra admin center**.
2. Go to **Identity > Applications > App registrations > New registration**.
3. Name it **KWI Booth Availability Sync**.
4. Select **Accounts in this organizational directory only**.
5. Register the application.
6. On the app's **Overview** page, copy the **Application (client) ID**.
7. Open this project's `appsettings.json` and replace:

   `PASTE-CLIENT-ID-HERE`

   with that client ID.

## Configure laptop interactive sign-in

1. In the app registration, open **Authentication**.
2. Select **Add a platform**.
3. Select **Mobile and desktop applications**.
4. Add/select the redirect URI **http://localhost**.
5. Save.

## Add Microsoft Graph permissions for testing

1. Open **API permissions**.
2. Select **Add a permission > Microsoft Graph > Delegated permissions**.
3. Add **Sites.ReadWrite.All**.
4. Grant admin consent if your tenant requires it.

The first `preview.bat` run should open a Microsoft sign-in page. Azure Identity stores a protected local token cache so later test runs can normally sign in silently until Microsoft requires reauthentication.

## Important

Do not create a client secret for the laptop test.

For the server, we will switch the same project to `Auth.Mode = Certificate` and restrict app-only SharePoint access. Do that only after the laptop preview and live sync have been verified.

# Privacy

I respect your personal privacy as much as I do my own. For that reason, you should be aware of the following before using RSS Reader.


### What data gets sent to RSS Reader?
For the sole purpose of enabling app functionality, RSS Reader stores the following data:

* Your Microsoft account email address
* Feeds that you subscribe to on RSS Reader
* Feed item metadata (text content, tags, unread posts, saved posts)



You are trusting __ME__ and the __RSS Reader code & infrastructure__ with the above data. If you prefer not to, you can use an anonymized Microsoft account or self-host this solution. See [LOCAL-TESTING.md](LOCAL-TESTING.md) for steps to build and run locally.

### We do not share or sell any personal data.

### How is that data used?
* This data is kept private and used solely for app functionality.
* No third-party analytics or ad services are provisioned in the infrastructure.
* No data is shared with third parties except Microsoft/Azure as the cloud provider.
 
### A personal Microsoft account is required.
A personal Microsoft account is required to login to RSS Reader. The only account data we use is the email address. It serves as your username in RSS Reader.
Microsoft securely handles the authentication when you login from your browser. Your password is never exposed to RSS Reader.

### We won't spam you.
We don't send any emails at all.


# Security

* Data (user emails, feeds, posts) are stored on an encrypted Azure Storage Account.
* The backend API and database run in an Azure Container App, inside a managed environment, with logs sent to Azure Log Analytics (retained for 30 days).
* All network traffic to the backend is protected by HTTPS only (no insecure connections allowed).
* All requests to the backend server must come from an authenticated user, and require a secret API key. The API is not accessible without proper credentials.
* The backend API prevents users from seeing any data that isn't their own.

---

## Requesting Account Deletion (Coming Soon)

A feature to allow users to delete their account and all associated data is planned. Once available, you will be able to self-remove your account and personal data directly from the app. For now, please create an issue and I will reach out to you for details.

## GDPR Notice

If you are an EU resident, the following applies:

- **Legal Basis:** Data is processed solely to provide app functionality and authentication, based on your consent when you use RSS Reader.
- **Your Rights:** You have the right to access, correct, delete, restrict, or object to the processing of your personal data. You may also request a copy of your data.
- **Data Retention:** Your data is retained only as long as your account exists. __Once the account deletion feature is available,__ you will be able to remove your data at any time.
- **International Transfers:** Data is stored in Microsoft Azure data centers in the United States.
- **Contact:** To exercise your rights or for privacy concerns, please open an issue in this repository.
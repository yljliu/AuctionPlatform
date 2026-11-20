# 14 Accounts: Design

## 1. Data (migration `AddAccounts`)

`ops.EmailOutbox`: `EmailId` bigint PK, `To` nvarchar(256), `Template` nvarchar(40), `Subject`, `Body` (text), `CreatedAt`. Index (`To`, `CreatedAt` desc). Rows older than 7 days are deleted by a workers job.

`ident.UserTokens`: `TokenId` PK, `UserId`, `Purpose` (VerifyEmail, ResetPassword), `TokenHash` binary(32), `ExpiresAt`, `UsedAt` null, `CreatedAt`. Index (`TokenHash`) unique; index (`UserId`, `Purpose`).

`ident.SellerApplications`: `ApplicationId` PK, `UserId`, `DisplayName`, `AccountHolder`, `AccountNumber` nvarchar(34) (cleared on review), `AccountLast4` char(4), `AddressJson`, `Status` (Pending, Approved, Rejected), `Reason` null, `ReviewedBy` null, `CreatedAt`, `ReviewedAt` null. Filtered unique (`UserId`) WHERE `Status = 0`.

`ident.Users.EmailVerifiedAt` and `UserStatus.PendingVerification` already exist.

## 2. Tokens (`Auction.Infrastructure/Identity/UserTokenService`)

```
Issue(user, purpose, ttl): plain = 32 random bytes, base64url; store SHA-256(plain); return plain
  ResetPassword: UPDATE older unused tokens of the user SET UsedAt = now   (R3.2)
Use(plain, purpose): row by hash and purpose, UsedAt null, ExpiresAt > now -> set UsedAt (one UPDATE ... WHERE UsedAt IS NULL, rowcount 1)
  else TOKEN_INVALID
```

Links: `{Site:BaseUrl}/account/verify?token=...` and `/account/reset?token=...` (`Site:BaseUrl` = `http://localhost:5001` lite, `http://localhost:5000` full).

## 3. Email (`Auction.Infrastructure/Email/DemoMailer`)

`IEmailSender.SendAsync(to, template, model)`; `DemoMailer` renders plain text from a small template table and inserts `ops.EmailOutbox`. Templates: `verify`, `register-exists`, `reset`, `password-changed`, `seller-approved`, `seller-rejected`. Called inside the business transaction when one exists, so a rolled-back change sends nothing.

## 4. Flows

```
Register (spec 01 AuthManager.RegisterAsync):
  new email:   user PendingVerification; token VerifyEmail 24 h; mail verify
  used email:  no user; mail register-exists to the owner; same reply
Login: password ok and PendingVerification -> EMAIL_NOT_VERIFIED (after the password check, so it leaks nothing new)
ResendVerification(username, password): limiter "verify:{user}" 3/h; new token; mail verify
VerifyEmail(token): Use -> user Active, EmailVerifiedAt = now
RequestPasswordReset(email): limiter "reset:{email}" 3/h (over: do nothing); user found -> token 30 min; mail reset; always OK
ResetPassword(token, new): policy; Use; hash; revoke all refresh tokens; clear lockout; mail password-changed
ApplyForSeller(user, ...): Active, not Seller, no Pending, 7 days since last Rejected; insert Pending
Review(admin, id, approve, reason):
  approve: tran: role Seller; SellerProfiles (Verified, DisplayName); AccountLast4; AccountNumber = null; audit; outbox SellerApproved; mail
  reject:  reason required; Status Rejected; AccountNumber = null; audit; outbox SellerRejected; mail
```

## 5. Contracts

`auth.proto`: `VerifyEmail`, `ResendVerification`, `RequestPasswordReset`, `ResetPassword`. `account.proto` (spec 11): `ApplyForSeller`, `GetSellerApplication`. `admin.proto`: `ListSellerApplications`, `ReviewSellerApplication`. `demo.proto` (Api, only when `Email:DemoMailbox`): `ListEmails(to)`.

## 6. Notifications

| Type | User | Title |
|---|---|---|
| `SellerApproved = 26` | applicant | "You can now sell" |
| `SellerRejected = 27` | applicant | "Your seller application was not approved: {reason}" |

## 7. Web

| Page | Change |
|---|---|
| `Account/Register.razor` | After submit: "Check your email" with a link to the demo mailbox. |
| `Account/Login.razor` | `EMAIL_NOT_VERIFIED` → message and "Send a new link"; "Forgot password?" link. |
| `Account/Verify.razor`, `Account/Forgot.razor`, `Account/Reset.razor` (new, static like login) | As R2, R3. |
| `Demo/Mailbox.razor` (new) | As R1.2. |
| `Me/BecomeSeller.razor` (new) | Form and latest status. Account menu "Become a seller" for non-sellers. |
| Admin page | Seller applications tab: list, Approve / Reject with a reason. |

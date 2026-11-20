# 14 Accounts: Tasks

- [ ] 1. Data, tokens and demo mailer
  - Entities, migration `AddAccounts`; `UserTokenService`; `DemoMailer` and templates; outbox cleanup job; `ListEmails` and `/demo/mailbox` behind `Email:DemoMailbox`.
  - Tests (unit): token is stored hashed; templates fill in links. (integration): a token works once; expired → `TOKEN_INVALID`; mailbox off → 404.
  - _Requirements: R1, R5.2_

- [ ] 2. Email verification
  - Register as `PendingVerification`; `VerifyEmail`, `ResendVerification`; login `EMAIL_NOT_VERIFIED`; seed and bots stay Active.
  - Tests (integration): register → Pending and one `verify` email; verify → Active; login before → `EMAIL_NOT_VERIFIED`; 4th resend in an hour → `RATE_LIMITED`; used email → same reply and a `register-exists` email, no new user.
  - _Requirements: R2_

- [ ] 3. Password reset
  - `RequestPasswordReset`, `ResetPassword`.
  - Tests (integration): unknown email → OK, no email; known → `reset` email; second request → first token dead; reset → new password works, old refresh tokens revoked, lockout cleared, `password-changed` email; token twice → `TOKEN_INVALID`; suspended user stays suspended.
  - _Requirements: R3_

- [ ] 4. Seller applications
  - `ApplyForSeller`, `GetSellerApplication`, `ListSellerApplications`, `ReviewSellerApplication`; notifications 26, 27.
  - Tests (integration): apply twice → `ALREADY_APPLIED`; seller applies → `INVALID_STATE`; duplicate display name → `VALIDATION_FAILED`; approve → role Seller, profile Verified, account number cleared, last4 kept; reject without a reason → `VALIDATION_FAILED`; apply again within 7 days → `INVALID_STATE`.
  - _Requirements: R4_

- [ ] 5. Web pages
  - Verify, Forgot, Reset, Mailbox, BecomeSeller pages; login and register changes; admin Seller applications tab.
  - Tests (bUnit): Forgot always shows the same message; Reset enforces 12+ characters; Become a seller hidden for sellers.
  - _Requirements: R2.2–R2.4, R3.1, R4.3, R4.6_

- [ ] 6. Demo check
  - Register "carla" → mailbox → click verify → log in. Forgot password for carla → mailbox → reset → old sessions signed out. As carla, Become a seller; as admin, approve; carla sees Seller pages after the next refresh and lists an item.
  - _Requirements: all_

# 14 Accounts: Requirements

Covers: email verification (design W7), password reset (W8) and seller applications (W9). There is no real email provider: emails go to a demo mailbox page. TOTP, email preferences and digests stay out of scope.

## R1 Demo mailbox
1. THE SYSTEM SHALL write every email (to, subject, text body, links, time) to `ops.EmailOutbox` instead of sending it.
2. WHILE `Email:DemoMailbox` is true (on in both compose files) THE site SHALL show `/demo/mailbox`: enter an email address and see the last 20 emails to it, newest first, with clickable links. WHEN it is false the page SHALL return 404.
3. THE mailbox page SHALL say "Demo mailbox: emails are not really sent".

## R2 Email verification (W7)
1. WHEN a user registers THE SYSTEM SHALL create them as `PendingVerification` and email a verify link with a one-time token valid for 24 hours.
2. WHEN the link is opened with a valid token THE SYSTEM SHALL set the user `Active` and the email verified, and show "Email verified. You can log in."
3. A used, unknown or expired token SHALL show "This link has expired" and a "Send a new link" button.
4. WHEN a `PendingVerification` user logs in with the right password THE SYSTEM SHALL return `EMAIL_NOT_VERIFIED`, and the login page SHALL offer "Send a new link". At most 3 links per hour per user (`RATE_LIMITED`).
5. Registering an email that is already used SHALL look the same to the visitor as a new one (spec 01) and SHALL email the owner "Someone tried to register with your email" instead.
6. Seeded users and bots SHALL stay `Active` and verified.

## R3 Password reset (W8)
1. "Forgot password" SHALL ask for an email and SHALL always reply "If an account exists, we sent a link", whether or not it exists.
2. FOR an existing account THE SYSTEM SHALL email a reset link with a one-time token valid for 30 minutes. A new request SHALL make older tokens unusable. At most 3 per hour per email (silently ignored above that).
3. WHEN the user sets a new password with a valid token THE SYSTEM SHALL apply the password policy (spec 01, 12+ characters), save it, mark the token used, revoke all their sessions (all refresh tokens), clear failed-login lockout, and email "Your password was changed".
4. A used, unknown or expired token SHALL return `TOKEN_INVALID`.
5. Reset SHALL NOT lift a suspension or ban.

## R4 Become a seller (W9)
1. An `Active` user without the Seller role SHALL be able to apply: display name (3 to 40 characters, unique among sellers), payout details (account holder name and a 4 to 34 character account number; only the last 4 are kept after review), and an address (spec 11 address fields).
2. A user SHALL have at most one pending application (`ALREADY_APPLIED`). A user who is already a seller SHALL get `INVALID_STATE`.
3. THE admin page SHALL have a Seller applications tab listing pending ones, oldest first.
4. WHEN an admin approves THE SYSTEM SHALL add the Seller role, create a `SellerProfiles` row (`Verified`), keep only the last 4 of the account number, write an audit entry, and notify and email the user "You can now sell". The user's next page load SHALL show the seller pages (role read from a new access token at refresh).
5. WHEN an admin rejects (a reason is required) THE SYSTEM SHALL keep the user a buyer, store the reason, and notify and email the user with it. The user MAY apply again after 7 days.
6. THE application page SHALL show the status of the latest application (Pending, Approved, Rejected with the reason).

## R5 Observability
1. THE SYSTEM SHALL count `emails_total{template}`, `verifications_total{result}`, `password_resets_total{result}` and `seller_applications_total{result}`.
2. Tokens SHALL be stored hashed (SHA-256); the plain token SHALL only be in the email. Logs SHALL NOT contain tokens or account numbers.

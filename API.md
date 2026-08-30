# Exam Archive API — frontend reference

Covers only the routes you asked for. Everything here was read off the current
`mysql-provider` branch and the response examples are real responses from a running
instance, not sketches.

---

## 1. Before you write any code

### Base URL

| Environment | URL |
| --- | --- |
| Local (HTTPS) | `https://localhost:7294` |
| Local (HTTP) | `http://localhost:5229` |

**Use the HTTPS one.** The session cookie is issued with `Secure`, so over plain
HTTP the browser accepts the login response and then silently throws the cookie
away — sign-in looks like it worked and every later request arrives anonymous.
This is the single most common way to lose an afternoon on this API.

If the dev certificate is not trusted yet, run `dotnet dev-certs https --trust` once.

### `credentials: 'include'` on every request

Auth is a **cookie**, not a bearer token, and the cookie is `HttpOnly` — JavaScript
cannot read it, store it, or attach it manually. The browser will only send it if you
ask, on *every* call including the login itself:

```ts
const api = (path: string, init: RequestInit = {}) =>
  fetch(`${import.meta.env.VITE_API_URL}${path}`, {
    ...init,
    credentials: 'include',           // required, always
    headers: { 'Content-Type': 'application/json', ...init.headers },
  });
```

Miss it on login and no cookie is stored. Miss it on a later call and you are
anonymous for that call only — which produces a confusing "I am logged in but the API
disagrees" bug.

> For `multipart/form-data` uploads, **do not** set `Content-Type` yourself — let the
> browser set it so it can add the `boundary`. See §6.4.

### CORS

The dev origins already allowlisted in `appsettings.Development.json`:

```
http://localhost:5173   https://localhost:5173     (Vite)
http://localhost:3000   https://localhost:3000     (CRA)
```

Running on a different port? Add it there — a wildcard is not an option, because a
CORS policy that carries credentials must name its origins explicitly.

---

## 2. Conventions

### 2.1 Every list is wrapped

All six listing endpoints return the same envelope. Write the unwrapping once.

```jsonc
{
  "data": [ /* rows */ ],
  "meta": { "page": 1, "perPage": 10, "totalItems": 16, "totalPages": 2 }
}
```

Query params `page` and `perPage` are accepted by every listing:

| Param | Default | Rules |
| --- | --- | --- |
| `page` | `1` | Clamped to ≥ 1. `page=0` is treated as page 1. |
| `perPage` | `10` | Clamped to 1–100. `perPage=1000` serves 100, and `meta.perPage` says `100`. |

Past the last page is **an empty `data` array with honest totals, not a 404** — so a
client holding a stale page number can see it overshot and step back.

Filling a dropdown? Ask for `?perPage=100` and be done in one request.

### 2.2 Enum encoding — read this carefully

Enums are **not** encoded consistently, and it is deliberate:

| Enum | Wire format | Example |
| --- | --- | --- |
| `ExamType` | **string** | `"Final"` |
| `PaperStatus` | **string** | `"Pending"` |
| `UserRole` | **number** | `4` |

So `/api/me` returns `"role": 4`, not `"role": "User"`. Don't write a generic enum
parser that assumes one or the other.

In **query strings and form fields**, `ExamType` is accepted case-insensitively:
`examType=Final`, `examType=final`, `examType=FINAL` all work.

### 2.3 Error shapes

Two shapes, both RFC 7807.

**Validation (400)** — has an `errors` map keyed by field:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Files": ["A paper cannot have more than 10 pages."]
  },
  "traceId": "00-66c572443d086da1..."
}
```

**Everything else** — no `errors` map:

```json
{
  "title": "Sign-in failed",
  "detail": "The username or password is incorrect.",
  "status": 401
}
```

> ⚠️ **Error keys are not consistently cased.** Query-parameter errors come back
> camelCase (`"subjectId"`, `"majorId"`, `"yearOfStudy"`); body and form errors come
> back PascalCase (`"SubjectId"`, `"Year"`, `"Files"`); per-file upload errors are
> indexed (`"Files[0]"`, zero-based, though the *message* counts pages from 1).
> **Match these case-insensitively** when mapping errors onto form fields.

### 2.4 401 does not mean "your session expired"

The most important quirk in this API.

A signed-in account that lacks the required role gets **401, not 403**. That is a
deliberate choice — a 403 would confirm to a curious student that the staff route
exists and that some other account may use it. One answer for both cases reveals
nothing.

The consequence for your React app: **a global "on 401 → redirect to /login"
interceptor will loop.** A logged-in student who clicks an Approve button gets 401,
gets bounced to login, signs in successfully, and lands back on the same 401.

Handle it like this:

```ts
async function handle(res: Response) {
  if (res.status === 401) {
    // Ask who we are before deciding what 401 meant.
    const me = await fetch(`${API}/api/me`, { credentials: 'include' });
    if (me.ok) throw new ForbiddenError();   // signed in, just not allowed
    throw new UnauthenticatedError();        // genuinely not signed in
  }
  // ...
}
```

Cheaper alternative: keep the current user in context after login, and if you already
know you are signed in, treat a 401 from a staff route as "not permitted" without the
extra round trip. Only fall back to `/api/me` when you have no user in state.

**One real 403 remains**, and it is unrelated: an account on an admin-issued temporary
password is blocked from everything except changing it.

```json
{
  "title": "Password change required",
  "status": 403,
  "detail": "This account is using a password issued by an administrator. Change it at /api/change-password before continuing."
}
```

You will only see this for staff accounts created by an admin. Route those users to a
change-password screen. (`POST /api/change-password` is outside this doc's scope —
body is `{ currentPassword, newPassword }`, min 12 characters.)

---

## 3. Types

Drop this in `src/api/types.ts`.

```ts
// ---------- enums ----------

/** Serialized as a STRING. */
export type ExamType = 'Midterm' | 'Final' | 'Resit';

/** Serialized as a STRING. */
export type PaperStatus = 'Pending' | 'Approved' | 'Rejected';

/** Serialized as a NUMBER. Lower value = more privilege. */
export enum UserRole {
  SuperAdmin = 1,
  Admin      = 2,
  Moderator  = 3,
  User       = 4,
}

/** Anyone who works the review queue. The gate on every staff action. */
export const isStaff = (r: UserRole) => r <= UserRole.Moderator;

/** May delete papers. */
export const isAdmin = (r: UserRole) => r <= UserRole.Admin;

// ---------- envelope ----------

export interface PageMeta {
  page: number;
  perPage: number;
  totalItems: number;
  totalPages: number;
}

export interface Paged<T> {
  data: T[];
  meta: PageMeta;
}

export interface PageParams {
  page?: number;       // default 1,  clamped >= 1
  perPage?: number;    // default 10, clamped 1..100
}

// ---------- auth ----------

export interface LoginRequest {
  username: string;    // 1..50
  password: string;    // 1..128
}

export interface CurrentUser {
  id: number;
  username: string;
  role: UserRole;      // number, see above
}

// ---------- reference data ----------

export interface Studies {
  id: number;
  nameSr: string;
  nameEn: string | null;
}

export interface Major {
  id: number;
  nameSr: string;
  nameEn: string | null;
  studiesId: number;
}

export interface Subject {
  id: number;
  code: string | null;
  nameSr: string;
  nameEn: string | null;
  yearOfStudy: number;   // 1..6, a property of the major+subject pairing
}

// ---------- papers ----------

/** One page of a paper. */
export interface PaperFile {
  pageNumber: number;    // 1-based, reading order
  contentType: string;   // "application/pdf" | "image/jpeg" | "image/png" | "image/webp"
  sizeBytes: number;
}

/**
 * Pages grouped by format, keyed by extension without the dot.
 * A format with no pages is ABSENT, not an empty array — treat missing and empty alike.
 */
export type PaperFiles = Partial<Record<'pdf' | 'jpg' | 'png' | 'webp', PaperFile[]>>;

/** List row — GET /api/papers */
export interface Paper {
  id: number;
  subjectId: number;
  subjectNameSr: string;
  subjectNameEn: string | null;
  examType: ExamType;
  month: number;                    // 1..12
  year: number;                     // calendar year of the exam, NOT year of study
  pageCount: number;
  uploadedAt: string;               // ISO 8601 UTC
  status: PaperStatus;
  reviewedAt: string | null;
  rejectionReason: string | null;   // null unless rejected
}

/** Detail — GET /api/papers/{id}, and the body returned by approve / reject. */
export interface PaperDetail extends Paper {
  files: PaperFiles;
}

/** Upload receipt. NOTE: `files` here is a FLAT array, not grouped. */
export interface UploadedPaper {
  id: number;
  subjectId: number;
  examType: ExamType;
  month: number;
  year: number;
  uploadedAt: string;
  status: PaperStatus;              // "Approved" for staff, "Pending" otherwise
  files: PaperFile[];               // <-- flat, unlike PaperDetail.files
  claimToken: string | null;        // shown ONCE, null for staff uploads
}

export interface RejectPaperRequest {
  reason: string;                   // 3..500 chars, required, shown to the submitter
}

// ---------- errors ----------

export interface ProblemDetails {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  traceId?: string;
}

export interface ValidationProblemDetails extends ProblemDetails {
  errors: Record<string, string[]>;
}
```

### The one shape inconsistency to watch

`PaperDetail.files` is an **object grouped by format**. `UploadedPaper.files` is a
**flat array**. Same field name, different shape, different endpoints. If you write
one shared render function for "a paper's files", normalise at the boundary.

---

## 4. Who can do what

| | anon | User (4) | Moderator (3) | Admin (2) | SuperAdmin (1) |
| --- | :--: | :--: | :--: | :--: | :--: |
| `GET /studies` `/majors` `/subjects` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `GET /papers` (approved) | ✅ | ✅ | ✅ | ✅ | ✅ |
| `GET /papers?status=Pending\|Rejected` | 401 | 401 | ✅ | ✅ | ✅ |
| `GET /papers/{id}` — approved | ✅ | ✅ | ✅ | ✅ | ✅ |
| `GET /papers/{id}` — pending/rejected | 404 | 404 | ✅ | ✅ | ✅ |
| `POST /papers/upload` | 401 | ✅ → Pending | ✅ → Approved | ✅ → Approved | ✅ → Approved |
| `POST /papers/{id}/approve` `/reject` | 401 | 401 | ✅ | ✅ | ✅ |
| `DELETE /papers/{id}` | 401 | 401 | **401** | ✅ | ✅ |

Note the two different "no" answers on the detail route: unapproved papers are **404**
for anyone who may not see them, never 403 or 401. That is intentional — a distinct
code would let anyone probe ids to discover that a pending submission exists.

Gate your UI on `isStaff(user.role)` and `isAdmin(user.role)`. A moderator sees the
Delete button 401 if you show it to them.

---

## 5. Auth routes

### 5.1 `POST /api/login`

Anonymous. Issues the session cookie.

**Request**

```json
{ "username": "moderator", "password": "password" }
```

**200**

```json
{ "id": 2, "username": "moderator", "role": 3 }
```

**401** — wrong username *or* wrong password; deliberately the same message for both,
so an attacker cannot use it to enumerate real accounts.

```json
{ "title": "Sign-in failed", "detail": "The username or password is incorrect.", "status": 401 }
```

**400** — missing/oversized fields, `errors` keyed `"Username"` / `"Password"`.

```ts
export async function login(body: LoginRequest): Promise<CurrentUser> {
  const res = await api('/api/login', { method: 'POST', body: JSON.stringify(body) });
  if (!res.ok) throw new Error((await res.json()).detail ?? 'Sign-in failed');
  return res.json();
}
```

Dev accounts (Development environment only, created by `SeedData`, password `password`):
`superadmin`, `admin`, `moderator`, `student`, `milica`, `stefan`, `jovana`.

---

### 5.2 `POST /api/logout`

Anonymous — signing out an already-expired session **succeeds quietly** rather than
answering 401, so you never have to handle a failure that does not matter.

No request body. **Always 204 No Content.** Just clear local state after it resolves.

---

### 5.3 `GET /api/me`

Requires a session. This is how a freshly-loaded page discovers whether it is signed
in — the cookie is `HttpOnly`, so you cannot inspect it directly.

**200**

```json
{ "id": 3, "username": "student", "role": 4 }
```

**401** — not signed in, *or* the cookie is from an older version of the sign-in code
and is missing a claim. Either way: send them to sign in again.

Call this once on app boot to hydrate your auth context.

---

## 6. Paper routes

### 6.1 `GET /api/papers`

Anonymous for approved papers. The same endpoint is the **staff review queue** via
`?status=`.

**Query parameters**

| Param | Type | Required | Notes |
| --- | --- | --- | --- |
| `subjectId` | int ≥ 1 | no | **The only filter that narrows by hierarchy.** Omit to browse the whole archive. |
| `studiesId` | int ≥ 1 | no | Verified, *not* filtered on — see below. |
| `majorId` | int ≥ 1 | no | Verified, *not* filtered on. |
| `yearOfStudy` | int 1–6 | no | Verified, *not* filtered on. Requires `majorId`. |
| `examType` | `ExamType` | no | `Midterm` \| `Final` \| `Resit`, case-insensitive. |
| `month` | int 1–12 | no | Month the exam was held. |
| `year` | int | no | Calendar year of the exam. Unbounded — an unlikely year matches nothing rather than 400ing. |
| `status` | `PaperStatus` | no | Defaults to `Approved`. **Anything else requires staff.** |
| `page`, `perPage` | int | no | §2.1. |

**Why `studiesId` / `majorId` / `yearOfStudy` exist if they don't filter**

They are the *path the user walked* to reach the subject, and that path cannot be
recovered from the subject alone — Databases is taught in three majors, in two
different years, so `subjectId=5` cannot tell a reloaded page which dropdowns to
restore. Carrying them makes a search URL a complete, shareable, bookmarkable
description of the search.

They are **verified rather than ignored**: a path the server has agreed is consistent
is one you can safely draw a breadcrumb from. A contradiction is a 400 that names the
real value, so a stale bookmark can be repaired rather than only reported broken:

```json
{
  "status": 400,
  "errors": {
    "yearOfStudy": ["Subject 5 is taught in year 2 of major 1, not year 3."]
  }
}
```

Other cascade errors: `"SubjectId is required when studiesId, majorId or yearOfStudy is given."`,
`"MajorId is required when yearOfStudy is given."`,
`"Major 1 does not belong to studies 2."`,
`"Subject 5 is not taught in major 3."`

**Ordering** follows the status, not the caller:

- `status=Pending` → **oldest upload first** (a work queue; nothing should rot at the bottom).
- anything else → **newest exam first** (year desc, month desc, id desc).

**200**

```json
{
  "data": [
    {
      "id": 8,
      "subjectId": 1,
      "subjectNameSr": "Математика I",
      "subjectNameEn": "Mathematics I",
      "examType": "Midterm",
      "month": 11,
      "year": 2025,
      "pageCount": 1,
      "uploadedAt": "2025-11-26T10:00:00Z",
      "status": "Approved",
      "reviewedAt": "2025-11-28T10:00:00Z",
      "rejectionReason": null
    }
  ],
  "meta": { "page": 1, "perPage": 10, "totalItems": 16, "totalPages": 2 }
}
```

**401** — non-staff asked for `status=Pending` or `status=Rejected`. Refused rather
than quietly forced back to `Approved`, so you never get a silently different answer
than the one you asked for.

Examples:

```
GET /api/papers?subjectId=5
GET /api/papers?studiesId=1&majorId=1&yearOfStudy=2&subjectId=5&examType=Final
GET /api/papers?subjectId=5&year=2024&month=6
GET /api/papers?status=Pending&perPage=25          ← the moderator queue
GET /api/papers?page=2&perPage=5
```

---

### 6.2 `GET /api/papers/{id}`

Anonymous for approved papers; staff see any status.

**200** — note `files` is grouped by format, and formats with no pages are absent:

```json
{
  "id": 30,
  "subjectId": 5,
  "subjectNameSr": "Базе података",
  "subjectNameEn": "Databases",
  "examType": "Final",
  "month": 6,
  "year": 2025,
  "pageCount": 9,
  "uploadedAt": "2026-08-30T15:31:12Z",
  "status": "Approved",
  "reviewedAt": "2026-08-30T15:31:12.4Z",
  "rejectionReason": null,
  "files": {
    "pdf": [
      { "pageNumber": 1, "contentType": "application/pdf", "sizeBytes": 142106 },
      { "pageNumber": 2, "contentType": "application/pdf", "sizeBytes": 653 }
    ],
    "jpg": [
      { "pageNumber": 3, "contentType": "image/jpeg", "sizeBytes": 3196896 },
      { "pageNumber": 4, "contentType": "image/jpeg", "sizeBytes": 329 }
    ],
    "png": [
      { "pageNumber": 8, "contentType": "image/png", "sizeBytes": 91 }
    ]
  }
}
```

`pageNumber` is global across the paper and preserves reading order, so a mixed paper
groups as `pdf: [1,2]`, `jpg: [3,4,5,6,7]`, `png: [8,9]`. To render in order, flatten
and sort by `pageNumber`; to offer "download the PDF", read `files.pdf`.

**404** — no such paper, **or** it exists but is not visible to you. You cannot tell
these apart, by design.

---

### 6.3 `DELETE /api/papers/{id}`

**Admin or SuperAdmin only.** A Moderator gets 401.

Permanently removes the paper, its file rows, and the bytes on disk. This is *not*
rejection — rejection is a reversible judgement that keeps the files. Use this only
for papers that should not exist at all: duplicates, withdrawal requests, a scan
carrying something personal.

No request body.

| Status | Meaning |
| --- | --- |
| `204` | Deleted. No body. |
| `401` | Not signed in, or not an administrator. |
| `404` | No such paper. |

Irreversible — put a confirmation dialog on it.

---

### 6.4 `POST /api/papers/upload`

**Any signed-in account.** `multipart/form-data`.

The role on the cookie decides the outcome, not anything in the form:

- **Staff** → published immediately, `status: "Approved"`, `claimToken: null`,
  plus a `Location: /api/papers/{id}` header.
- **Everyone else** → queued, `status: "Pending"`, a `claimToken` is issued, and **no
  `Location` header** (a pending paper has no public address yet — `GET /api/papers/{id}`
  would 404 for the very account that just created it).

**Form fields**

| Field | Type | Notes |
| --- | --- | --- |
| `Files` | file (repeated) | One entry per page, **in reading order** — order comes from the order the parts arrive. |
| `SubjectId` | int | Must exist. |
| `ExamType` | string | `Midterm` \| `Final` \| `Resit`. |
| `Month` | int | 1–12. |
| `Year` | int | 1990 … next year (a January sitting is often filed against the academic year). |

**Limits**

| Rule | Value |
| --- | --- |
| Files per submission | **10** |
| Of which PDFs | **at most 2** |
| Per file | 20 MB |
| Whole submission | 100 MB |
| Accepted | `.pdf` `.jpg` `.jpeg` `.png` `.webp` |

Formats **may be mixed** — a scanned PDF alongside photographed pages is one paper.
The extension is treated as a claim and confirmed against the file's magic bytes, so a
renamed file is rejected. Images have their EXIF stripped before anything touches disk
(GPS coordinates and device ids do not belong in a public archive); PDFs pass through
unchanged.

```ts
export async function uploadPaper(form: {
  files: File[]; subjectId: number; examType: ExamType; month: number; year: number;
}): Promise<UploadedPaper> {
  const fd = new FormData();
  form.files.forEach(f => fd.append('Files', f));   // repeat the same key, in order
  fd.append('SubjectId', String(form.subjectId));
  fd.append('ExamType', form.examType);
  fd.append('Month', String(form.month));
  fd.append('Year', String(form.year));

  const res = await fetch(`${API}/api/papers/upload`, {
    method: 'POST',
    credentials: 'include',
    body: fd,                        // NO Content-Type header — the browser adds the boundary
  });
  if (!res.ok) throw await res.json();
  return res.json();
}
```

**201**

```json
{
  "id": 29,
  "subjectId": 5,
  "examType": "Final",
  "month": 6,
  "year": 2025,
  "uploadedAt": "2026-08-30T15:31:10Z",
  "status": "Pending",
  "files": [
    { "pageNumber": 1, "contentType": "application/pdf", "sizeBytes": 142106 },
    { "pageNumber": 2, "contentType": "image/jpeg", "sizeBytes": 329 }
  ],
  "claimToken": "K7M4-P2QX-9RTB"
}
```

> **`claimToken` appears exactly once, here.** The server stores only a hash of it and
> can never show it again. A UI that does not put it in front of the submitter has
> silently thrown it away. Show it prominently — not in small grey text — because it is
> how someone checks on a submission from a device they are not signed in on.
> (`GET /api/papers/status/{token}` — outside this doc's scope.)

**400** — real messages you will need to render:

```jsonc
{ "errors": { "Files":     ["A paper cannot have more than 10 pages."] } }
{ "errors": { "Files":     ["A submission may contain at most 2 PDFs, and this one has 3."] } }
{ "errors": { "Files":     ["The submission exceeds the 100 MB total limit."] } }
{ "errors": { "Files[0]":  ["Page 1 exceeds the 20 MB per-file limit."] } }
{ "errors": { "Files[1]":  ["Page 2 must be one of: .jpeg, .jpg, .pdf, .png, .webp."] } }
{ "errors": { "Files[1]":  ["Page 2 is not a valid JPG file."] } }
{ "errors": { "Files[2]":  ["Page 3 could not be read as an image. It may be damaged."] } }
{ "errors": { "Files[0]":  ["Page 1 is empty."] } }
{ "errors": { "SubjectId": ["Subject 999 does not exist."] } }
{ "errors": { "Year":      ["Year must be between 1990 and 2027."] } }
```

`Files[n]` is **zero-based** but the message says "Page n+1". Map the index to your
file list; show the message as-is.

**401** — not signed in.
**415** — you set `Content-Type` manually, or omitted it. Let the browser do it.

---

### 6.5 `POST /api/papers/{id}/approve`

**Staff only.** No request body.

Publishes the paper — it becomes visible to everyone, signed in or not. Also reverses
a rejection, clearing the stored reason (leaving it would put a rejection note on a
published paper).

Approving an already-approved paper **changes nothing and still returns 200**, so a
double-clicked button is not an error.

**200** — the full `PaperDetail`, so you can update your row in place without refetching:

```json
{
  "id": 13, "subjectId": 6,
  "subjectNameSr": "Оперативни системи", "subjectNameEn": "Operating Systems",
  "examType": "Midterm", "month": 4, "year": 2025,
  "pageCount": 1, "uploadedAt": "2025-04-14T21:33:00Z",
  "status": "Approved", "reviewedAt": "2026-08-30T15:40:02.1Z", "rejectionReason": null,
  "files": { "pdf": [ { "pageNumber": 1, "contentType": "application/pdf", "sizeBytes": 651 } ] }
}
```

**401** not staff · **404** no such paper.

---

### 6.6 `POST /api/papers/{id}/reject`

**Staff only.**

**Request**

```json
{ "reason": "Page 2 is upside down and unreadable. Please re-photograph it." }
```

`reason` is **required, 3–500 characters**. It is written *for the submitter* and is
the only feedback they ever get, so treat the field as user-facing copy in your UI —
not an internal note.

Rejecting an already-rejected paper **updates the reason**, which is what a moderator
correcting their own wording expects.

The files stay on disk. Rejection is reversible: approving later restores the paper.

**200** — the full `PaperDetail`, with `status: "Rejected"` and `rejectionReason` set.

**400**

```json
{ "errors": { "Reason": ["The reason must be between 3 and 500 characters."] } }
```

**401** not staff · **404** no such paper.

---

## 7. Reference-data routes

All three are anonymous and paged. They exist to drive the search cascade.

### 7.1 `GET /api/studies`

No filters — there is nothing above a level of study to narrow it by. Two rows today.

```json
{
  "data": [
    { "id": 1, "nameSr": "Основне академске студије", "nameEn": "Bachelor's" },
    { "id": 2, "nameSr": "Мастер академске студије",  "nameEn": "Master's" }
  ],
  "meta": { "page": 1, "perPage": 10, "totalItems": 2, "totalPages": 1 }
}
```

### 7.2 `GET /api/majors`

| Param | Required | Notes |
| --- | --- | --- |
| `studiesId` | no | Omit to list every major. |
| `page`, `perPage` | no | |

```json
{
  "data": [
    { "id": 1, "nameSr": "Рачунарске науке",        "nameEn": "Computer Science",      "studiesId": 1 },
    { "id": 2, "nameSr": "Софтверско инжењерство",  "nameEn": "Software Engineering",  "studiesId": 1 },
    { "id": 3, "nameSr": "Електротехника",          "nameEn": "Electrical Engineering","studiesId": 1 }
  ],
  "meta": { "page": 1, "perPage": 10, "totalItems": 3, "totalPages": 1 }
}
```

### 7.3 `GET /api/subjects`

| Param | Required | Notes |
| --- | --- | --- |
| `majorId` | **yes** | 400 without it — `yearOfStudy` is a property of the major+subject pairing and means nothing alone. |
| `yearOfStudy` | no | 1–6. Mostly unnecessary, see below. |
| `page`, `perPage` | no | |

```json
{
  "data": [
    { "id": 1, "code": "MAT101", "nameSr": "Математика I",    "nameEn": "Mathematics I", "yearOfStudy": 1 },
    { "id": 2, "code": "MAT120", "nameSr": "Линеарна алгебра", "nameEn": "Linear Algebra", "yearOfStudy": 1 },
    { "id": 5, "code": "IT240",  "nameSr": "Базе података",    "nameEn": "Databases",      "yearOfStudy": 2 }
  ],
  "meta": { "page": 1, "perPage": 10, "totalItems": 8, "totalPages": 1 }
}
```

Omitting `majorId` returns 400 with an unusual key casing — note it is camelCase here,
unlike body/form errors:

```json
{
  "status": 400,
  "errors": { "majorId": ["A value for the 'majorId' parameter or property was not provided."] }
}
```

**Ask for the whole curriculum once.** A single major's curriculum fits one page at
`perPage=100`, and every row carries its `yearOfStudy` — so one request fills both the
Year dropdown and the Subject dropdown, and switching years needs no round trip. The
`yearOfStudy` filter exists for callers who would rather ask than filter locally.

### Sorting is your job

None of these sort by name, deliberately: the correct order depends on the language
and script the reader chose, and only the client knows that. `studies` and `majors`
come ordered by id; `subjects` by year then id.

```ts
subjects.sort((a, b) => a.nameSr.localeCompare(b.nameSr, 'sr'));
```

---

## 8. The search cascade

The intended flow, each step holding an id from the one before:

```
GET /api/studies                    → Bachelor's (1)
GET /api/majors?studiesId=1         → Computer Science (1)
GET /api/subjects?majorId=1&perPage=100
                                    → whole curriculum; fills Year AND Subject pickers
GET /api/papers?studiesId=1&majorId=1&yearOfStudy=2&subjectId=5
                                    → the papers
```

Put all four ids in your URL query string. That makes the page reloadable and
shareable, and the server has already verified the path is consistent — so you can
render a breadcrumb from it and trust it.

---

## 9. ⚠️ Known gap: nothing serves file bytes

**There is currently no endpoint that returns the contents of a file.**

`GET /api/papers/{id}/files` and `GET /api/papers/{id}/files/{pageNumber}` were both
removed. `GET /api/papers/{id}` tells you a paper has 9 pages, that pages 1–2 are PDFs
and 3–7 are JPEGs, and how many bytes each is — but there is no URL that hands any of
them over.

What this means for the frontend:

- You **cannot** build a paper viewer, an image gallery, a PDF embed, or a download
  button. There is nothing to point them at.
- Moderators **cannot** open a submission to judge whether it is a legible exam paper.
  Review is metadata-only: page count, formats, sizes.

Build everything else against this doc; leave a placeholder where the viewer goes. When
a serving route is added it will most likely be `GET /api/papers/{id}/pages/{pageNumber}`
with an optional `?download=true`, and the natural change here is a `url` field on each
`PaperFile` — so keep your file rendering behind one component and this stays a small
change.

The server-side machinery for it is still intact and unused (`PaperFileServer`,
path-traversal guard, `Content-Disposition` handling, `X-Content-Type-Options: nosniff`),
so restoring it is adding a route, not rebuilding the feature.

---

## 10. Quick checklist

- [ ] Point at **`https://`**, not `http://` — otherwise the cookie is dropped silently.
- [ ] `credentials: 'include'` on **every** request, login included.
- [ ] Your dev origin is in `Cors:AllowedOrigins`.
- [ ] `role` is a **number**; `examType` and `status` are **strings**.
- [ ] Don't blind-redirect on 401 — check `/api/me` first, or you will loop.
- [ ] Unwrap `{ data, meta }` once, centrally.
- [ ] `perPage=100` for dropdowns.
- [ ] Sort names client-side with `localeCompare(_, 'sr')`.
- [ ] Never set `Content-Type` on the upload.
- [ ] Show `claimToken` loudly — it is never shown again.
- [ ] `PaperDetail.files` is grouped; `UploadedPaper.files` is flat.
- [ ] Match validation error keys case-insensitively.

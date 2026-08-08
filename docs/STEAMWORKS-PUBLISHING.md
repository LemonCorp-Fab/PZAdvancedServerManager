# Authorized Steamworks Workshop publishing

## Decision status

This document describes the preferred long-term publishing architecture for PZ Advanced Server Manager.

- **Target provider:** the native Steamworks `ISteamUGC` API using the account already connected to the Steam desktop client.
- **Current provider:** SteamCMD remains implemented, supported, and unchanged.
- **Implementation status:** design only. The native provider must not be enabled until Valve and The Indie Stone grant the required application permissions.
- **Fallback:** SteamCMD remains necessary for headless hosts and installations where the authorized native provider is unavailable.

The purpose of the native provider is to let an administrator create and update Project Zomboid Workshop items with the same Steam account that owns the game, without entering Steam credentials in PZASM and without starting a competing SteamCMD user session.

## Why SteamCMD remains the current provider

SteamCMD is currently the only deployable publishing mechanism available to an independent tool without cooperation from the game publisher. It can create or update an item from a VDF manifest and records the assigned `publishedfileid` for later updates.

Valve describes `workshop_build_item` as a testing-oriented integration because it requires a Steam account login. Its login is separate from the desktop Steam client session and can therefore interrupt or compete with the session used to launch games. Preserving SteamCMD's portable `config` directory reduces repeated authentication, but it cannot turn SteamCMD into a desktop-session API.

PZASM must keep the current provider until the authorization gate described below has been completed. No migration may remove a working SteamCMD project, session, schedule, or CLI workflow.

Official reference: [Steam Workshop implementation guide — SteamCMD integration](https://partner.steamgames.com/doc/features/workshop/implementation#SteamCmd).

## Required authorization

An independent executable cannot grant itself permission to publish to another application's Workshop.

The proper configuration requires all of the following:

1. PZASM, or a dedicated PZASM publishing companion, receives its own Steam application/tool AppID.
2. The Indie Stone adds that AppID to the Project Zomboid Workshop **App Publish Permissions** for consumer AppID `108600`.
3. Steam Cloud quota is enabled for both the Project Zomboid Workshop configuration and the publishing tool AppID as required for preview storage.
4. The resulting Steamworks configuration is published by the authorized application owners.
5. Valve confirms that the tool may ship the Steamworks redistributable for this purpose.

Valve explicitly documents the separate-editor model and the App Publish Permissions requirement in the [Workshop implementation guide](https://partner.steamgames.com/doc/features/workshop/implementation#FAQ).

PZASM must not impersonate AppID `108600`, reuse Project Zomboid's `steam_api64.dll`, copy Steam client tokens, inject code into the game, or depend on a development-only `steam_appid.txt` workaround. The released integration must use its assigned tool AppID and the permission granted by the Workshop owner.

## User and ownership model

The native provider uses the Steam user already connected to the local Steam client:

- the user keeps the same Project Zomboid license;
- no second account or second purchase is required;
- PZASM never receives the user's password or Steam Guard code;
- the created Workshop item belongs to the connected user, not to LemonCorp or a shared service account;
- updates are allowed only when the connected user owns the target Workshop item and the tool is authorized for the Project Zomboid Workshop.

The project must record the owner SteamID returned by Steam when an item is created or first verified. Before every update, PZASM must compare the active SteamID with that stored owner. An ownership mismatch is a blocking error with a clear account-switch instruction; PZASM must never create a replacement item automatically.

## Native publication flow

The native provider uses the [official `ISteamUGC` interface](https://partner.steamgames.com/doc/api/ISteamUGC) and processes asynchronous results through regular Steam callbacks.

### Session initialization

1. Detect that the Steam desktop client is running.
2. Initialize Steamworks under the authorized PZASM tool AppID.
3. Verify that `SteamUser()->BLoggedOn()` is true.
4. Read the current SteamID and display the account identity before publication.
5. Verify that the Workshop publishing interface is available.
6. Keep pumping Steam callbacks for the entire operation.

PZASM must not show username, password, Steam Guard, QR-code, or cookie-import fields for this provider. Authentication belongs entirely to the Steam client.

### Creating a new item

For a project whose Workshop ID is zero:

1. Validate and build the package before calling Steam.
2. Call `ISteamUGC::CreateItem(108600, k_EWorkshopFileTypeCommunity)`.
3. Wait for `CreateItemResult_t` and map its `EResult` value to a user-facing result.
4. Persist the returned `PublishedFileId_t` immediately, even before uploading content.
5. Persist the active owner SteamID and the provider identifier.
6. If `m_bUserNeedsToAcceptWorkshopLegalAgreement` is true, stop before content publication and open the official item or legal-agreement page for the user.
7. Resume by updating that same item after the agreement has been accepted.

An item ID must never be discarded because a later content upload failed. A retry always updates the saved item.

### Updating item content

For a saved Workshop ID:

1. Verify the active SteamID against the stored owner.
2. Call `ISteamUGC::StartItemUpdate(108600, publishedFileId)`.
3. Set the generated title with `SetItemTitle`.
4. Set the exhaustive generated description with `SetItemDescription`.
5. Set the metadata language with `SetItemUpdateLanguage`.
6. Set visibility with `SetItemVisibility`.
7. Set supported tags when Project Zomboid's Workshop configuration permits them.
8. Set the final Workshop content directory with `SetItemContent`.
9. Set the validated preview image with `SetItemPreview`.
10. Submit with `SubmitItemUpdate` and the project's change note.
11. Poll `GetItemUpdateProgress` and expose the exact preparing, content upload, preview upload, and commit phases in the existing operation dialog.
12. Wait for `SubmitItemUpdateResult_t`, persist the result, and update `LastPublishedAt` only after success.

The provider must retain the same package preflight currently used by SteamCMD: the content directory must exist, contain at least one file, use the final atomic build path, and include a valid preview before any Steam call can create or modify remote state.

### Cancellation semantics

Local preparation can be cancelled safely before `SubmitItemUpdate`. Valve documents no cancellation method once `SubmitItemUpdate` has started. The UI must therefore change from **Cancel** to **Hide / continue in background** after submission and explain that closing PZASM can leave the final result unknown.

## Existing SteamCMD-created items

Existing projects and their Workshop IDs must remain intact.

An authorized prototype must test whether an item originally created through SteamCMD with creator/consumer AppID `108600` can be updated by the separate authorized tool AppID when the connected SteamID is the owner. The expected migration path is:

1. load the existing project and Workshop ID;
2. query the item details;
3. verify the owner SteamID;
4. perform a metadata-only update on a private test item;
5. perform a small content update;
6. retain SteamCMD for that project if Steam returns `AccessDenied`, an incompatible creator-AppID result, or any unconfirmed ownership state.

PZASM must never clone, delete, transfer, or replace an existing item as an automatic migration step.

## Provider architecture

Publishing must remain behind a provider boundary so the current implementation and the future native integration can coexist.

```text
PackageLifecycleService
    |
    +-- IWorkshopPublishingProvider
            |
            +-- SteamCmdPublishingProvider       active everywhere today
            |
            +-- SteamUgcPublishingProvider       authorized desktop builds only
            |
            +-- SteamOAuthPublishingProvider     optional future headless provider
```

The shared provider contract should expose capabilities instead of relying on provider names:

```csharp
public interface IWorkshopPublishingProvider
{
    WorkshopPublishingCapabilities Capabilities { get; }
    Task<PublisherIdentity> GetIdentityAsync(CancellationToken cancellationToken);
    Task<PublishResult> PublishAsync(
        PackageProject project,
        PackageBuildResult build,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}
```

Capabilities should include:

- create new item;
- update existing item;
- unattended execution;
- requires desktop Steam;
- supports interactive authentication;
- supports reliable cancellation before submission;
- supports progress by byte count and Steam update phase.

`PackageLifecycleService` remains responsible for validation, build selection, immediate Workshop ID persistence, scheduling locks, and optional server coordination. A provider is responsible only for Steam identity, remote creation/update, progress, and provider-specific error mapping.

## Project schema additions

The project format should eventually add backward-compatible fields similar to:

```json
{
  "publishing": {
    "provider": "steamcmd",
    "publishedWorkshopId": 3779519478,
    "ownerSteamId": null,
    "consumerAppId": 108600,
    "creatorAppId": null,
    "workshopAgreementPending": false,
    "lastProviderResult": null
  }
}
```

Migration rules:

- existing projects default to `steamcmd`;
- existing `PublishedWorkshopId` values are copied without modification;
- selecting the native provider requires a successful identity and ownership verification;
- provider switching never clears the Workshop ID;
- SteamCMD credentials and portable sessions remain outside project JSON;
- OAuth tokens, if later supported, remain outside project JSON and lockfiles.

## Desktop UI and CLI behavior

The provider selector must present availability truthfully:

- **SteamCMD — available:** current cross-platform and headless provider;
- **Steam client — authorization required:** shown as unavailable in public builds until the tool AppID has permission;
- **Steam client — connected as _display name_:** shown only after successful native initialization;
- **Steam Web authorization — unavailable:** shown only if Valve has not issued the required OAuth client.

For the native provider, the publication confirmation must display:

- connected Steam display name and SteamID;
- target Workshop ID or **New item**;
- consumer AppID `108600`;
- visibility;
- content file count and byte size;
- whether legal-agreement acceptance may be required;
- an explicit warning when the current account differs from the recorded owner.

The CLI must return a dedicated exit code when desktop Steam is absent, another for owner mismatch, and another for Workshop agreement acceptance. It must never fall back to SteamCMD silently because doing so changes the authentication and session-impact model.

## Scheduling and headless limitations

Native `ISteamUGC` is appropriate for a desktop machine where Steam is running and the publishing account is logged in. A scheduled native publication can run only while that condition remains true.

It is not a complete replacement for:

- a Linux VPS without a graphical Steam client;
- a containerized automation worker;
- a server whose publisher account is intentionally not logged into the desktop client;
- offline package preparation.

For these environments, SteamCMD remains the supported provider unless Valve grants an OAuth client and confirms a complete Workshop content-upload Web API workflow.

## Optional Steam OAuth path

Valve documents an OAuth 2.0 flow for partner applications, including AppID-scoped `read_cloud` and `write_cloud` permissions for Workshop access. Valve must issue the OAuth Client ID after reviewing the requested permissions, token lifetime, redirect URI, and AppIDs. See the [official Steam OAuth documentation](https://partner.steamgames.com/doc/webapi_overview/oauth).

OAuth would provide the best future model for unattended or non-Steam-client hosts, but it must remain a separate investigation because the publicly documented `IPublishedFileService` reference does not currently expose the same complete create/content/preview/submit workflow as native `ISteamUGC`.

Before implementing an OAuth provider, Valve must confirm:

1. that PZASM may obtain `write_cloud` for AppID `108600`;
2. the exact endpoints for creating an item and uploading a folder payload;
3. whether a publisher key and secure backend are also required;
4. token renewal, revocation, and maximum lifetime;
5. whether a local loopback redirect URI is accepted for desktop and CLI use;
6. whether scheduled use is allowed under the granted terms.

Steam OpenID and a personal Web API key are not substitutes. OpenID proves identity but grants no Workshop write permission. Publisher-key methods must run on a secure publisher backend and cannot be shipped in a desktop client.

## Security requirements

The native provider must:

- accept no Steam password, Steam Guard code, session cookie, or desktop token;
- avoid reading or modifying Steam client configuration files;
- verify the connected SteamID before every remote mutation;
- store only non-secret identity and result metadata in the project;
- isolate Steamworks interop behind a small audited module;
- validate every content and preview path before passing it to Steam;
- ensure all paths remain inside the selected final build root;
- redact sensitive provider diagnostics before writing logs;
- open only official Steam legal-agreement and item URLs;
- fail closed if the tool AppID, consumer AppID, or callback result is unexpected.

If OAuth is added later, tokens must be encrypted with the platform secret store, must never appear in logs or project exports, and must be revocable from the UI and CLI.

## Packaging and licensing constraints

The Steamworks native runtime is not a normal open-source dependency. Before adding it to PZASM releases, the project must review:

- the Steamworks SDK and redistributable terms;
- compatibility with PZASM's repository license;
- whether the native binaries may be redistributed outside Steam;
- whether separate Windows and Linux tool builds are authorized;
- whether a third-party .NET binding is sufficiently maintained and license-compatible;
- whether a small first-party C ABI bridge is preferable to a large wrapper dependency.

No Project Zomboid binary or Steamworks binary copied from the user's game installation may be committed or redistributed by PZASM.

Official references: [Steamworks SDK](https://partner.steamgames.com/doc/sdk) and [open-source distribution considerations](https://partner.steamgames.com/doc/sdk/uploading/distributing_opensource).

## Error mapping

The provider should preserve Steam's `EResult` value internally and present a specific recovery action. At minimum, it must distinguish:

- Steam client not running;
- user not logged on;
- tool AppID not authorized;
- access denied or owner mismatch;
- Workshop legal agreement required;
- invalid or missing content;
- invalid preview or Cloud quota failure;
- account temporarily restricted from uploading;
- duplicate request;
- network timeout with unknown final state;
- service unavailable or read-only;
- successful item creation followed by failed content submission.

Unknown results must display the numeric `EResult`, operation phase, saved Workshop ID, and a concise log path without exposing session data.

## Validation plan

The native provider cannot be considered production-ready until it passes all of the following with a dedicated private test item:

1. create an item while the normal Steam desktop client remains connected;
2. confirm that no password or Steam Guard prompt is handled by PZASM;
3. save the new Workshop ID before content upload;
4. accept the Workshop agreement when requested and resume safely;
5. upload Bundle content and preview;
6. update the same item without creating a duplicate;
7. update an existing SteamCMD-created item owned by the same user;
8. reject an item owned by another account;
9. survive PZASM restart after item creation and before content submission;
10. report all `GetItemUpdateProgress` phases;
11. handle Steam client logout and reconnection;
12. preserve SteamCMD operation for the same project after switching back;
13. validate Windows behavior;
14. validate Linux desktop behavior if authorized binaries are available;
15. confirm that Project Zomboid clients and dedicated servers install the resulting Workshop item exactly like a SteamCMD-published pack.

## Delivery phases

### Phase 0 — authorization

- obtain a tool AppID;
- request App Publish Permissions from The Indie Stone;
- confirm Cloud quota and redistribution terms with Valve;
- prepare private test accounts and items without requiring a second Project Zomboid purchase for end users.

### Phase 1 — provider abstraction

- extract the existing SteamCMD implementation behind `IWorkshopPublishingProvider`;
- keep all existing behavior and tests unchanged;
- add provider capabilities and project-schema migration.

### Phase 2 — native prototype

- add isolated Steamworks initialization and callback processing;
- implement identity, create, update, progress, and agreement handling;
- keep the provider behind an experimental build flag.

### Phase 3 — compatibility and UX

- test existing SteamCMD-created items;
- add provider selection, owner verification, and precise progress;
- add CLI capability and exit-code reporting;
- document desktop-only scheduling constraints.

### Phase 4 — supported release

- enable the native provider only in builds carrying the authorized tool AppID;
- keep SteamCMD selectable and fully supported;
- publish the authorization and privacy model;
- consider OAuth only after separate Valve approval and endpoint confirmation.

## Go/no-go criteria

The native provider is a **go** only if:

- The Indie Stone authorizes the PZASM tool AppID for Workshop `108600`;
- Valve authorizes SDK/runtime distribution for the tool;
- the connected desktop account can create and update private Project Zomboid items without disrupting the Steam client;
- existing item ownership can be verified reliably;
- the legal-agreement flow is complete;
- SteamCMD projects remain backward-compatible.

Without those conditions, the provider is a **no-go** for public releases and this document remains a proposal. SteamCMD continues as the active provider.


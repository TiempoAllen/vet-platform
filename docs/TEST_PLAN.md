# Test Plan

Veterinary Management Platform. Version 1.0, 2026-10-13, written at the B-04 merge.

## 1. Purpose and scope
Defines how the platform is verified: test levels, environments, what every milestone must test, entry and exit
criteria, and traceability from `SRS.md` requirements to tests. Covers the backend now and sets the rules the
frontend (F-01 onward) and end-to-end suites (X-02) will follow.

## 2. Test levels

| Level | Tooling | Runs against | Owner milestone |
|---|---|---|---|
| Unit | xunit | Pure functions (password policy, IP partition key, permission grants) | Each B milestone |
| API integration | xunit + `WebApplicationFactory<Program>` | In-memory host, real HTTP pipeline | Each B milestone |
| Database integration (`Category=Database`) | Same, plus Testcontainers PostgreSQL 18 (or `VETPLATFORM_TEST_POSTGRES`) | Real PostgreSQL, one database per test class | Each B milestone |
| Schema drift | `PersistenceConfigurationTests` | EF model vs latest migration | Always |
| Contract | Reviewer agent `api-contract-guardian`; `UserObject_HasExactlyTheDocumentedFields`-style tests; `openapi.yaml` lint | `API.md`, `openapi.yaml` | Each B milestone |
| Security review | Reviewer agent `security-tenancy-reviewer` | Diff of the milestone | Each B milestone |
| Data-model review | Reviewer agent `data-model-reviewer` | Entities and migrations | Milestones that change the schema |
| Frontend unit/component | Chosen in F-01 (e.g. Vitest + Testing Library) | Components, API client | Each F milestone |
| Accessibility | axe-core in component/E2E tests, manual keyboard pass | Screens | Each F milestone |
| End-to-end | Chosen in X-02 (e.g. Playwright) | Full stack, seeded database | X-02 |
| Performance | Chosen in X-04 (e.g. k6) | Staging-like environment | X-04 |
| Security verification | Dependency audit, secret scan, tenant-isolation sweep, header check | Release candidate | X-03 |
| Backup/restore drill | Runbook procedure | Staging | X-05, then quarterly |

No in-memory database provider: it hides constraint, transaction and SQL behaviour (ADR-002).

## 3. Test environments

| Environment | Purpose | Data |
|---|---|---|
| Developer machine | Fast feedback; `dotnet test` | Disposable containers |
| CI (to be set up; recommended GitHub Actions with a Docker-enabled runner) | Gate every push/PR | Disposable containers |
| Staging (X-05) | E2E, performance, restore drills | Synthetic data only; never production copies |
| Production | Smoke checks only (`/api/health`, sign-in with a monitoring account) | Real |

Test data: synthetic only. Tests create their own clinic per case (`factory.SignedInAsync(Role.Admin)`), so tests
never depend on each other. Never put real client, patient or financial data into any non-production environment.

## 4. How to run
From `backend/`:

```bash
dotnet build VetPlatform.slnx -warnaserror
dotnet test VetPlatform.slnx                                # everything; needs Docker or VETPLATFORM_TEST_POSTGRES
dotnet test VetPlatform.slnx --filter "Category!=Database"  # without PostgreSQL
dotnet list VetPlatform.slnx package --vulnerable --include-transitive
```

Time-dependent behaviour (idle timeout, lockout expiry) uses the injected fake clock (`factory.Clock`), never sleeps.

## 5. Mandatory test checklist per backend milestone
Every new or changed endpoint must have tests for:

1. **Happy path** with exact status, body shape (no undocumented fields) and headers (`Location` on 201).
2. **Authentication:** `401 UNAUTHENTICATED` without a session.
3. **Authorization:** `403 FORBIDDEN` for each role that lacks the permission (theory over roles), success for each
   role that has it.
4. **Tenant isolation:** another clinic's record is `404` for every operation (read, update, action); lists never
   include other clinics' rows; created rows belong to the caller's clinic.
5. **CSRF:** unsafe endpoints reject requests without a valid token (the generic test covers this if the endpoint
   is a controller action; `EveryUnsafeEndpoint_IsAControllerAction_SoTheCsrfFilterCoversIt` enforces that).
6. **Validation:** each field rule gives `422` with the right `field`; malformed JSON and unknown enum names give
   `400`; invalid query parameters give `422`.
7. **Business rules and conflicts:** each documented `409`, including concurrency where the rule depends on it
   (parallel requests, as in `TwoAdministratorsDemotingEachOtherAtOnce_LeaveOneAdministrator`).
8. **Audit:** the expected `audit_logs` row with actor, clinic, entity, request id and no secrets; no row when the
   change is rolled back.
9. **Database constraints:** invalid values rejected by the database even when the application is bypassed.
10. **Logging hygiene:** no secrets, personal data or query strings in logs for sensitive endpoints.
11. **Pagination, search, sort** on list endpoints, including stable ordering across pages.

Module-specific additions:

| Module | Extra tests |
|---|---|
| Scheduling | Overlap rejected, including two concurrent bookings for the same slot; boundary (end = next start) allowed; availability and exceptions; DST transitions in the clinic timezone; every invalid status transition. |
| Clinical | Completed consultation cannot be edited; addenda preserve history; only permitted roles prescribe; attachment type/size limits, authorised download only, no other clinic's file. |
| Billing | Totals, tax and discount rounding with `decimal`; client-supplied totals ignored; partial and full payments; concurrent payments keep balance correct; voids not deletes; invoice number uniqueness under concurrency. |
| Inventory | Stock equals the sum of movements; no negative stock (per Q-5); concurrent dispensing of the last unit; receiving a PO creates movements; low-stock and expiry queries at boundaries. |
| Reports | Totals match source data; clinic scope; permission. |
| Audit query | Only the caller's clinic; clinic-less rows hidden from clinic administrators. |

## 6. Frontend test requirements (from F-01)
- Components: loading, empty, success, validation-error, permission-denied, not-found and server-error states
  (`UI.md` section 8).
- API client: CSRF refresh-and-retry once on `CSRF_TOKEN_INVALID`; redirect to login after `401`; error envelope
  parsing; `X-Request-Id` surfaced in error reports.
- Forms: field-level errors from `details`, input preserved after errors, double-submit prevented.
- Accessibility: automated axe checks with zero serious/critical violations; keyboard-only pass on each new screen.
- Navigation hidden according to `permissions` from `/api/auth/me` (visibility only; backend tests prove
  enforcement).

## 7. End-to-end scenarios (X-02)
1. Operator creates a clinic → admin signs in → creates staff → staff signs in and changes password.
2. New patient workflow: find/create client → create patient → book appointment.
3. Appointment workflow: check-in → consultation → diagnosis, treatment, prescription, vaccination → complete.
4. Billing: invoice from consultation → partial payment → full payment → receipt.
5. Inventory: receive stock with lot and expiry → dispense → low-stock warning.
6. Security: session expiry returns user to login; disabled user is signed out; second clinic sees none of the
   first clinic's data.

## 8. Entry and exit criteria

**Entry (start testing a milestone):** scope agreed in `ROADMAP.md`; contract drafted in `API.md`; migration
generated.

**Exit (milestone done; matches the close-out workflow):**
- Build with `-warnaserror` passes.
- All tests pass, including database tests; no skipped tests without a linked reason.
- Vulnerability scan reports no known vulnerable packages, or each is recorded as an accepted risk.
- The three reviewer agents ran; every confirmed finding is fixed or recorded as an accepted risk in an ADR.
- `API.md`, `openapi.yaml`, `DATABASE.md` and the traceability matrix below are updated.

**Release exit (first production release):** `SECURITY.md` section 14 checklist complete, X-02 E2E suite green,
X-04 performance targets met (`SRS.md` NFR-PERF), restore drill succeeded within RTO.

## 9. Defect management
| Severity | Definition | Rule |
|---|---|---|
| S1 Critical | Cross-clinic data exposure, authentication bypass, data loss/corruption | Blocks merge and release; fix immediately; add a regression test. |
| S2 High | Wrong financial/clinical result, broken core workflow, missing authorization on one path | Blocks merge. |
| S3 Medium | Wrong error code/message, contract mismatch, degraded UX | Fix in milestone or record in the milestone notes. |
| S4 Low | Cosmetic | Backlog. |

Every fixed defect gets a regression test named after the behaviour it protects.

## 10. Traceability matrix (implemented requirements)
Tests live in `backend/tests/VetPlatform.Api.Tests/`.

| Requirement | Tests |
|---|---|
| FR-AUTH-1 | `AuthTests.Login_WithValidCredentials_SetsHardenedSessionCookie_AndMeReturnsTheUser`, `Login_IgnoresEmailCaseAndSurroundingSpaces`, `UnnormalisedStoredEmail_StillSignsIn` |
| FR-AUTH-2 | `AuthTests.FailedLogins_AllLookIdentical_SoAccountsCannotBeEnumerated` |
| FR-AUTH-3 | `AuthTests.RepeatedWrongPasswords_LockTheAccountTemporarily`, `ConcurrentWrongPasswords_StillLockTheAccount`, `SuccessfulLogin_ResetsTheFailedAttemptCount`; `UsersTests.Lockout_IsAudited_ExactlyOnce` |
| FR-AUTH-4 | `LoginRateLimitTests.TooManyLoginAttempts_Return429WithRetryAfter`; `CsrfAndAccessTests.LoginRateLimit_GroupsIpv6ClientsByTheirSlash64` |
| FR-AUTH-5 | `AuthTests.Session_ExpiresAfterIdleTimeout_ButActivityKeepsItAlive`, `Session_EndsAtItsAbsoluteLifetime_EvenWhenActive`, `DisablingAUser_EndsTheirExistingSessionsImmediately`, `SuspendingTheClinic_EndsItsUsersSessionsImmediately` |
| FR-AUTH-6 | `AuthTests.Logout_RevokesTheSessionServerSide_SoAReplayedCookieIsRejected`, `Logout_WithoutASession_StillSucceeds` |
| FR-AUTH-7 | `AuthTests.SigningInAgain_RevokesThePreviousSessionOfThatBrowser`; `B04ReviewTests.SigningInToAnotherClinicsAccount_FromASignedInBrowser_Works_AndRevokesThePreviousSession` |
| FR-AUTH-8 | `UsersTests.Me_ReturnsRolesAndTheirPermissions`, `UserWithNoRoles_CanStillSeeThemselves_ButNothingElse`; `CsrfAndAccessTests.Me_WithoutSession_Returns401Envelope_NotARedirect` |
| FR-AUTH-9 | `PasswordAndBootstrapTests.ChangePassword_*`; `B04ReviewTests.WrongCurrentPasswords_CountTowardsLockout_AndTheLockSignsEverySessionOut` |
| FR-USR-1 | `PasswordAndBootstrapTests.CreateAdmin_*`; `B04ReviewTests.CreateAdmin_RejectsUnknownOptionsAndInvalidEmails` |
| FR-USR-2 | `UsersTests.List_SupportsSearchFilterSortAndPagination`, `List_RejectsInvalidQueryParameters`; `B04ReviewTests.ListFilters_AcceptOnlyExactNames` |
| FR-USR-3 | `UsersTests.Create_Returns201WithLocation_AndTheNewUserCanSignIn`, `Create_RejectsInvalidInput`, `Create_WithAnEmailAlreadyUsedInAnyClinic_Returns409` |
| FR-USR-4 | `UsersTests.Update_ChangesOnlyTheGivenFields_AndEmptyPhoneClearsIt`; `B04ReviewTests.Patch_*`, `SetRoles_*` |
| FR-USR-5 | `UsersTests.DisablingAUser_SignsThemOutAndBlocksSignIn`; `B04ReviewTests.ReEnablingAUser_DoesNotReviveTheirOldSessions` |
| FR-USR-6 | `UsersTests.AnAdministrator_CannotDisableThemselves`, `TheLastActiveAdministrator_CannotBeDemoted_OrDisabled`, `TwoAdministratorsDemotingEachOtherAtOnce_LeaveOneAdministrator` |
| FR-USR-7 | `UsersTests.RoleChanges_ApplyToTheVeryNextRequest` |
| FR-CLI-1 | `ClientsTests.Create_Returns201WithLocation_AndNormalisesInput`, `Create_WithOnlyNames_LeavesEverythingElseEmpty`, `Create_RejectsInvalidFields_With422`, `Create_RejectsUnknownContactMethod_With400`, `Update_*` (including `Update_IgnoresIdClinicAndTimestampsInTheBody`), `Get_UnknownClient_Is404`, `AdministratorsAndReceptionists_CanManageClients`, `ClinicalStaff_CanReadClients_ButNotChangeThem` |
| FR-CLI-2 | `ClientsTests.List_SearchesNamesEmailAndPhone_CaseInsensitively`, `List_FiltersByStatus_AndSortsByName`, `List_SortsByCreatedAt_AndPages`, `List_RejectsInvalidParameters_With422` |
| FR-CLI-3 | `ClientsTests.Clients_CannotBeDeleted`, `Update_CanDeactivateAndReactivate` |
| FR-PAT-1 | `PatientsTests.Create_Returns201WithLocation_AndNormalisesInput`, `Create_WithOnlyRequiredFields_UsesDefaults`, `Create_RejectsInvalidFields_With422`, `Create_ReportsServerSideProblemsTogether`, `Create_RejectsUnknownEnumNames_With400`, `Microchips_AreUniqueWithinAClinic_ButNotAcrossClinics`, `Update_*`, `Deceased_RecordsADateOfDeath_AndLeavingDeceasedClearsIt`, `Patients_CannotBeDeleted`, `ClinicStaff_CanReadAndManagePatients` |
| FR-PAT-2 | `PatientsTests.Create_ForAnInactiveClient_Is422`, `Transfer_ToAnotherActiveClient_ChangesTheOwner_AndIsAudited`, `Transfer_ToAnInactiveClient_Is422_ButAnInactiveCurrentOwnerDoesNotBlockEdits` |
| FR-PAT-3 | `PatientsTests.List_SearchesPatientOwnerAndMicrochip`, `List_FiltersByClientSpeciesAndStatus_AndSorts`, `List_ByAnotherClinicsClient_IsEmpty`, `Update_ReportsFieldErrorsBeforeAMicrochipConflict`, `List_RejectsInvalidParameters_With422` |
| FR-PAT-4 | `AppointmentLifecycleTests.FinishedAppointments_AppearInThePatientHistory_NewestFirst_WithoutTheReason`, `NoShows_AppearInTheHistory_AndDisappearWhenUndone`; `PatientHistoryTests.NewPatient_HasARegisteredEntry_WithActorAndOwner`, `Updates_DoNotAppearYet`, `OwnerShown_IsTheOwnerAtRegistration_WithCurrentName`, `EntriesWithoutAnActorOrOwner_HaveNulls_AndAreNewestFirst`, `Pages_AreStable_AndValidated`, `TiesOnOccurredAt_AreBrokenByIdDescending`, `ClinicStaff_CanReadHistory`, `UsersWithoutPatientAccess_CannotReadHistory` |
| FR-SCH-1 | `SchedulingTests.Vet_ManagesTheirOwnWeeklyHours`, `Vets_CannotChangeAnotherVetsHours_ButAdministratorsCan`, `OverlappingBlocks_AreRejected_UnlessTheirDatesDoNotOverlap`, `InvalidBlocks_Are422`, `ExceptionDetails_AreVisibleOnlyToManagersAndTheVetConcerned`, `Closures_AreClinicWide_AdministratorOnly_AndVisibleToAll`, `InvalidExceptions_Are422`, `InstantsWithoutAnExplicitOffset_Are400`, `ExceptionList_FiltersByVetAndPeriod_AndAlwaysIncludesClosures`, `UnavailableIsNotAnInputType_Is400`, `Exceptions_CanBeMovedAndCancelled_ByTheVet`, `WorkingHours_AreWeeklyHoursMinusExceptionsAndClosures_InTheClinicTimeZone`, `WorkingHours_ValidateTheRange`; `WorkingHoursCalculatorTests.*` (DST, closures, effective dates, 24:00) |
| FR-SCH-2 | `AppointmentsTests.Create_Returns201_WithSummariesAndTheClinicsOffset`, `ClinicStaff_CanBookAndEdit`, `InvalidBookings_Are422`, `BadlyTypedValues_Are400`, `EarlierToday_CanStillBeRecorded`, `OnlyActivePatients_AndActiveVetsOfTheClinic_CanBeBooked`, `ChangingThePatient_MovesTheClientWithIt`, `Update_ChangesOnlyGivenFields_AndBlankClearsText`, `OnlyScheduledAppointments_CanBeEdited`, `Reschedule_IntoThePast_Is422`, `List_FiltersAndSortsByStart`, `List_RejectsInvalidParameters`, `Appointments_CannotBeDeleted` |
| FR-SCH-3 | `AppointmentsTests.OutsideWorkingHours_ExceptionsAndClosures_Conflict`, `DoubleBookings_ConflictForTheVetAndThePatient_ButBackToBackIsFine`, `Override_AcceptsEveryConflict_AndRecordsWhichOnes`, `Override_WithoutConflicts_IsNotRecorded`, `ConcurrentBookingsOfTheSameSlot_OnlyOneWins`, `Reschedule_RechecksConflicts_AndAMoveToAFreeSlotClearsTheOverride`, `Database_ExcludesOverlappingUnoverriddenBookingsOfOneVet`, `ConcurrentEditsOfOneAppointment_AreSerialised_SoTheCombinationIsChecked`, `ActiveStatuses_MatchTheExclusionConstraint` |
| FR-SCH-4 | `AppointmentLifecycleTests.CheckIn_ThenComplete_RecordsTimes_AndIsFinal`, `Cancel_RecordsWhoAndWhy_FromScheduledOrCheckedIn_AndFreesTheSlot`, `Cancel_NeedsAnExactCancelledBy`, `CheckIn_OnlyOnTheAppointmentsDay_AndCompleteOnlyAfterCheckIn`, `NoShow_OnlyAfterTheStart_AndUndoRestoresIt`, `UndoNoShow_IsRefused_WhenTheSlotWasBookedAgain`, `UndoNoShow_IsRefused_EvenIfTheBookingHadOverriddenADoubleBooking`, `LifecycleTimes_CarryTheClinicsOffset_AndHistoryTimesAreUtc`, `UndoCheckIn_SameDayOnly`, `ClinicalStaff_CanRunTheLifecycle`, `ConcurrentTransitions_AreSerialised`, `Database_KeepsStatusAndTimestampsConsistent` |
| FR-CLN-1 | `ConsultationsTests.Vet_OpensEditsAndCompletes_AndTheWeightUpdatesThePatient`, `Nurses_OpenAndEdit_ButOnlyVeterinariansComplete`, `InvalidOpenings_Are422`, `InactivePatients_AndOtherClinicsPatients_CannotBeSeen`, `OneOpenConsultationPerPatient_EvenUnderConcurrency`, `AppointmentLink_NeedsACheckedInAppointmentOfThePatient_OncePerAppointment`, `Discard_NeedsAReason_AndHidesTheConsultationFromTheDefaultList` |
| FR-CLN-2 | `ConsultationsTests.CompletedConsultations_AreFrozen_AndCorrectedOnlyByAddenda`, `Addenda_AreCappedPerConsultation`, `Database_RefusesToChangeOrDeleteAddenda`, `Database_RefusesToChangeACompletedConsultation`, `Database_RefusesAddendaOnUnsignedConsultations`, `WriteResponses_EqualALaterRead_EvenWithSubMicrosecondClocks` |
| FR-CLN-3 | `DiagnosesTreatmentsTests.DiagnosesAndTreatments_AreRecordedOnTheConsultation`, `MakingADiagnosisPrimary_ClearsTheOtherPrimary`, `ACodeSystemWithoutACode_Is422_EvenWhenAlsoMakingItPrimary`, `Caps_Are409`, `UnknownItems_Are404_EvenOnACompletedConsultation`, `ProblemList_OrdersByMostRecentlyCompletedConsultation`, `RemovingADiagnosis_UnlinksItsTreatments`, `InvalidItems_AreRejected`, `ItemsOfAnotherConsultation_AreNotFound_AndCannotBeLinked`, `AfterCompletion_OnlyAVetCanChangeADiagnosisStatus`, `DiscardedConsultations_FreezeTheirDiagnosesCompletely`, `ProblemList_ShowsDiagnosesOfCompletedConsultations_AndIsAudited`, `History_ShowsTheConsultationsDiagnoses`, `Database_FreezesItemsOfCompletedConsultations`, `Database_KeepsItemsOnTheirConsultationsPatient` |
| FR-CLN-4 | `PrescriptionsTests.AVet_PrescribesOnTheConsultation`, `OnlyVets_WritePrescriptions_AndClinicalRolesReadThem`, `InvalidPrescriptions_Are422`, `Edits_ChangeOnlyWhatIsSent_AndRemovingDeletes`, `RemovingADiagnosis_UnlinksItsPrescriptions`, `TheCap_Is409`, `AfterCompletion_APrescriptionIsFrozen_ButAVetCanCancelIt`, `DiscardedConsultations_FreezeTheirPrescriptionsCompletely`, `MedicationList_ShowsPrescriptionsOfCompletedConsultations_NewestFirst_AndIsAudited`, `History_ShowsTheConsultationsPrescriptions_WithTheirCurrentStatus`, `Database_FreezesPrescriptionsOfCompletedConsultations`, `Database_RefusesCancelledPrescriptionsOnOpenConsultations_AndInconsistentCancellations`, `Database_RefusesMovingClinicalItemsOutOfACompletedConsultation`, `Database_KeepsPrescriptionsOnTheirConsultationsPatient`; `PrescriptionsMigrationTests.RollingBackAndForward_RestoresAndReappliesTheTriggerFunctions` |
| FR-CLN-5 | `VaccinationsTests.ANurse_RecordsADoseGivenHere`, `DosesGivenElsewhere_NameThePlace_AndNoVeterinarian`, `InvalidDoses_AreRejected`, `AFutureDose_Is422`, `ConsultationLinks_MustBeANonDiscardedConsultationOfThePatient`, `InactivePatients_Are409_AndOtherClinicsPatientsAre404`, `Voiding_KeepsTheRecord_WithWhoWhenAndWhy`, `PatientList_IsNewestFirst_FiltersByStatus_AndIsAudited`, `DueList_ShowsTheLatestActiveDosePerVaccine_WithTheOwnersContact`, `History_ShowsDosesToClinicalReadersOnly_OnTheLocalAdministeredDay`, `Database_NeverChangesARecord_ExceptToVoidIt`, `Database_ALinkedInsert_WaitsForAConcurrentDiscard_AndIsThenRefused`, `Database_RefusesVoidedInserts_DiscardedLinks_AndInconsistentGivers` |
| FR-CLN-6 | `AttachmentsTests.ANurse_UploadsAPatientFile_AndItDownloadsSafely`, `AcceptedTypes_AreRecognisedByTheirContent`, `OtherContent_Is415_WhateverItsName`, `MalformedUploads_AreRejected`, `FilesOver20Megabytes_Are413`, `ConsultationAndClientFiles_FollowTheirOwners`, `Withdrawing_HidesTheFile_ButKeepsTheRecordAndTheContent`, `History_ShowsPatientAndConsultationFiles_ToClinicalReadersOnly`, `Database_NeverChangesAnAttachment_ExceptToWithdrawIt`, `Database_RefusesWithdrawnInserts_DiscardedLinks_AndTwoOwners` |
| FR-CLN-7 | `ClinicalNotesTests.ANurse_AddsANote_ThatReadsBackAndIsAudited`, `List_IsNewestFirst_AndAudited`, `InvalidNotes_AndBadConsultationLinks_Are422`, `Permissions_AndOtherClinics`, `History_ShowsNotes_ToClinicalReadersOnly`, `Database_KeepsNotesAppendOnly`, `Database_RefusesNotesOnDiscardedConsultations` |
| FR-CLN-8 | `ConsultationsTests.Admins_ReadButNeverWrite_AndReceptionAndInventoryHaveNoAccess`, `ClinicalReads_AreAudited_WithoutNoteText`, `CompletedConsultations_AppearInTheHistory_OnlyForClinicalReaders` |
| FR-SET-1 (time zone) | `SchedulingTests.ClinicTimeZone_DefaultsToUtc_AndAnAdministratorCanChangeIt`, `ClinicTimeZone_MustBeAKnownIanaId`, `OnlyAdministrators_ChangeTheClinic`; `PasswordAndBootstrapTests.CreateAdmin_SetsTheClinicTimeZone_OnlyFromAnIanaId` |
| FR-AUD-1 | `UsersTests.UserChanges_AreAudited_WithActorClinicAndRequestId`, `AuthenticationEvents_AreAudited`; `B04ReviewTests.FailedSignInFromASignedInBrowser_IsNotAttributedToTheSignedInUser`; `ClientsTests.Changes_AreAudited_WithoutPersonalData`; `PatientsTests.Changes_AreAudited_WithoutNamesOrNotes` |
| NFR-SEC-1 | `CsrfAndAccessTests.ControllerEndpoints_RequireSignInByDefault`, `Me_WithGarbageSessionCookie_Returns401` |
| NFR-SEC-2 | `UsersTests.NonAdministrators_CannotReadOrManageUsers`, `Administrators_OnlyEverSeeTheirOwnClinicsUsers`, `OtherClinicsUsers_AreNotFound_ForEveryOperation`, `CreatedUsers_AlwaysBelongToTheCreatorsClinic`, `WithoutACurrentClinic_ClinicOwnedQueriesReturnNothing`, `SavingAnotherClinicsRow_FromWithinAClinicRequest_Throws`; `ClientsTests.UsersWithoutClientAccess_CannotReadOrManageClients`, `Staff_OnlyEverSeeTheirOwnClinicsClients`, `OtherClinicsClients_AreNotFound_AndUnchanged`, `CreatedClients_BelongToTheCreatorsClinic_WhateverTheBodySays`; `PatientsTests.UsersWithoutPatientAccess_CannotReadOrManagePatients`, `Staff_OnlyEverSeeTheirOwnClinicsPatients`, `OtherClinicsPatients_AreNotFound_AndUnchanged`, `AnotherClinicsClient_CannotBeChosenAsOwner_OnCreateOrTransfer`, `Database_RefusesAPatientLinkedToAnotherClinicsClient`, `Update_IgnoresIdClinicAndTimestampsInTheBody`; `PatientHistoryTests.OtherClinicsAndUnknownPatients_Are404`, `AuditEntriesOfAnotherClinic_NeverAppear_EvenForTheSamePatientId`, `AnotherClinicsUserOrClient_NamedOnThisClinicsEntry_IsNeverResolved`, `AuditQueriesWithoutACurrentClinic_ReturnNothing`; `SchedulingTests.UsersWithoutScheduleAccess_AreForbidden`, `NoPermission_IsForbiddenBeforeBodyValidation`, `NonVets_DisabledVets_AndOtherClinicsVets`, `OtherClinicsExceptions_AreNotFound`, `Vets_CannotTouchAnotherVetsExceptionOrAClosure`, `AVetsOwnIdWithAnotherVetsBlock_IsNotFound`, `AnotherClinicsVetAndBlocks_AreNotFound_OnEveryPath`, `Veterinarians_ListsOnlyThisClinicsActiveVets`, `Database_RefusesABlockForAnotherClinicsUser`; `AppointmentLifecycleTests.UsersWithoutAppointmentsManage_AreForbidden_AndOtherClinicsAppointmentsAreNotFound`, `AnotherClinicsPatientHistory_WithFinishedAppointments_IsNotFound`; `ConsultationsTests.OtherClinicsConsultations_AreNotFound`, `DiagnosesTreatmentsTests.OtherClinics_AreNotFound`, `Permissions_FollowTheClinicalRoles`, `ConsultationsTests.AnotherClinicsVetOrAppointment_CannotBeReferenced`; `PrescriptionsTests.UnknownAndForeignPrescriptions_Are404`, `OnlyVets_WritePrescriptions_AndClinicalRolesReadThem`; `VaccinationsTests.InactivePatients_Are409_AndOtherClinicsPatientsAre404`, `Permissions_ClinicalRolesRecord_ReceptionOnlySeesTheDueList`; `AttachmentsTests.OtherClinicsOwnersAndFiles_AreNotFound`, `ConsultationAndClientFiles_FollowTheirOwners`; `ClinicalNotesTests.Permissions_AndOtherClinics`; `AppointmentsTests.UsersWithoutAppointmentAccess_AreForbidden`, `OtherClinicsAppointments_AreInvisible`, `Patch_WithAnotherClinicsPatientOrVet_Is422_AndListFiltersWithTheirIdsAreEmpty`, `Database_RefusesAnotherClinicsPatientOrVet`; `AuthTests.ForgedCookie_PairingAValidSessionWithAnotherIdentity_IsRejected` |
| NFR-SEC-3 | `CsrfAndAccessTests.CsrfEndpoint_*`, `UnsafeRequest_*`, `EveryUnsafeMethod_RequiresACsrfToken`, `EveryUnsafeEndpoint_IsAControllerAction_SoTheCsrfFilterCoversIt`, `Login_WithoutCsrfToken_IsRejectedBeforeCredentialsAreChecked`; `AuthTests.CsrfToken_*`, `AfterSessionExpiry_TheStaleCsrfTokenIsRejected_*` |
| NFR-SEC-4 | `AuthTests.Login_WithValidCredentials_SetsHardenedSessionCookie_AndMeReturnsTheUser` |
| NFR-SEC-5 | `PasswordAndBootstrapTests.ChangePassword_EnforcesThePasswordPolicy`; `UsersTests.Create_RejectsInvalidInput` |
| NFR-SEC-6 | `AuthTests.Credentials_AreNeverWrittenToLogs`; `RequestLoggingTests.QueryStringValues_AreNeverLogged` |
| NFR-SEC-7 | `UsersTests.AuditLog_IsAppendOnly_InTheDatabase`; `B04ReviewTests.AuditRecordedAt_IsAlwaysTheDatabaseClock`, `AuditEntries_CannotReferenceUnknownClinicsOrUsers`, `RollingBackPastB04_IsRefusedWhileTheAuditLogHasEntries` |
| NFR-SEC-8 | `ErrorHandlingTests.*`; `ClientsTests.TextWithANulCharacter_IsRejectedWith400_NotA500` |
| NFR-SEC-9 | `AttachmentsTests.OtherContent_Is415_WhateverItsName`, `FilesOver20Megabytes_Are413`, `ANurse_UploadsAPatientFile_AndItDownloadsSafely`, `FileNames_AreReducedToASafeDownloadName_WithTheDetectedExtension` |
| NFR-REL-2 | `AuthTests.Database_RejectsInvalidUserValues`, `Database_RejectsAnEmailDifferingOnlyInCase_EvenWhenAWriterSkipsNormalisation`; `ClientsTests.Database_RejectsInvalidClientValues` (status, contact method, blank names, upper-case email); `PatientsTests.Database_RejectsInvalidPatientValues`; `SchedulingTests.Database_RejectsInvalidExceptions`, `Database_RejectsInvalidBlocks`; `AppointmentsTests.Database_RejectsInvalidAppointments`; `ConsultationsTests.Database_KeepsConsultationsConsistent` |
| NFR-REL-5 | `DatabaseTests.Health_IsAnonymousAndReportsHealthy_WhenDatabaseIsReachable`; `HealthEndpointTests.Health_Returns503Unhealthy_WhenDatabaseIsUnreachable` |
| NFR-OPS-1 | `RequestIdTests.*` |
| NFR-OPS-4 | `PersistenceConfigurationTests.Model_HasNoChangesMissingFromMigrations`; `DatabaseTests.Migrations_ApplyToAnEmptyDatabase_AndLeaveNothingPending` |
| NFR-OPS-5 | `PersistenceConfigurationTests.Startup_Fails_WhenConnectionStringIsMissing` |
| Contract (API.md section 4) | `B04ReviewTests.UserObject_HasExactlyTheDocumentedFields`, `SetRoles_AcceptsOnlyExactRoleNames`, `Patch_AcceptsOnlyExactStatusNames` |
| Contract (API.md section 5) | `ClientsTests.ClientObject_HasExactlyTheDocumentedFields`, `Me_ListsClientPermissions` |
| Contract (API.md section 6) | `PatientsTests.PatientObject_HasExactlyTheDocumentedFields`, `Me_ListsPatientPermissions`; `PatientHistoryTests.Entry_HasExactlyTheDocumentedFields` |

Requirements marked Planned in `SRS.md` are added to this matrix by the milestone that implements them.

## 11. Known gaps
- No CI pipeline yet; tests run locally. Recommended next step before Phase 2 grows the suite.
- No coverage measurement. Add `coverlet` reporting in CI; no hard threshold, but review uncovered branches in
  security-relevant code.
- No automated `openapi.yaml` validation; add a linter (e.g. Redocly CLI or Spectral) to CI.
- No load, E2E or restore tests yet (X-02, X-04, X-05).

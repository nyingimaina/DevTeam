## Q1
What should key the release now

**Answer:** The problem i have is that when i start a release, an automatic feature is created and assigned that very same release's name. This is wrong, no features should be auto created, instead we should land on an empty state screen, with a brief description and a CTA to create first feature


## Q2
Should this fix apply to hotfixes as well, or releases only

**Answer:** Relabel the single input to "Release Key" — release uses that key, features get their own keys later


## Q3
How should that CTA behave when clicked

**Answer:** Apply to both releases and hotfixes — same empty-state + CTA behavior


## Q4
With no feature yet, what should happen to branches at release/hotfix creation time

**Answer:** Expand an inline form right on the empty state (reuse the existing `CreateFeatureForm` / "Add feature" form)


## Q5
Which do you want on screen

**Answer:** Create no branches at all until the first feature is added


## Q6
What should happen to that existing checkout

**Answer:** "No features yet" heading + "This release doesn't have any features yet. Add one to begin the workflow." + button "Add feature"


## Q7
Now that this value is really the release key, how far should the rename go

**Answer:** Leave the previously active feature untouched — a featureless release doesn't disturb any checkout


## Q8
How wide should the sweep go

**Answer:** Rename end-to-end: endpoint/DTO field and client param to `ReleaseKey`/`releaseKey`


## Q9
Do you want me to do anything beyond this existing state

**Answer:** Wire fix + a regression test asserting the posted body uses `releaseKey`


## Q10
Which feature are you referring to

**Answer:** wait, is this feature already implemented?


## Q11
What would you like me to do next

**Answer:** the issue is when i create a release, DevTeam auto creates a feature which is named exactly as the release. The ideal flow should be, release is created, and no feature is auto created, but user is taken to a screen that informs them textually and graphically that there are currently no features in the release and gives them a CTA to create first feature, and the user can then create a feature that is named however they please


## Q12
Should the normal (non-empty) release keep its "＋ New feature" button label

**Answer:** Align the empty-state copy exactly to the spec


## Q13
Should that be renamed too

**Answer:** Rename it to "Add feature" everywhere


## Q14
What would you like me to do next

**Answer:** No, leave the legacy screen alone


## Q15
Should I commit that too

**Answer:** Commit these changes


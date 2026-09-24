## Q1
*Question 1: What should this installer package

**Answer:** are you able to iterate and find out yourself?


## Q2
*Question 2: Where should this feature's code live

**Answer:** Broker + a new Avalonia/WebView2 desktop shell delivered in this same install


## Q3
*Question 3: Where should the installer put the app, and does it need admin rights

**Answer:** Dedicated top-level packaging/ folder holding the .iss + build script, shell project beside it


## Q4
NET runtime be delivered

**Answer:** Per-user only — `{localappdata}\DevTeam`, no admin needed (matches ShipRight + `RuntimeIdentity.AppHomeDirectory`)


## Q5
*Question 5: How should the WebView2 runtime be handled

**Answer:** Framework-dependent — require the .NET 10 runtime, and have the installer detect/install it if missing


## Q6
What about its Windows lifecycle

**Answer:** Same as ShipRight — check registry, offer to download/install WebView2 if missing


## Q7
*Question 7: Where should the installer's version number come from

**Answer:** on demand only


## Q8
*Question 8: What should uninstall do with user data

**Answer:** Read it from `Directory.Build.props` (`InformationalVersion`) — one source of truth with the assemblies


## Q9
*Question 9: How deep should the automated verification of the installed app go

**Answer:** Remove program files, then ask the user whether to also delete their DevTeam data


## Q10
*Question 10: Which shortcuts should the installer create

**Answer:** Full UI automation


## Q11
*Question 11: What should the shell show at startup

**Answer:** Start Menu only


## Q12
*Question 12: How should the installer build be invoked

**Answer:** Straight into the web UI — the shell spawns the broker and loads the UI; workspace selection stays in the web UI


## Q13
*Question 13: How should the installer handle the `opencode` CLI, which DevTeam needs to actually run agents

**Answer:** 1


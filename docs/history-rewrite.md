# History rewrite of 2026-11-10

Before the repository was first pushed to GitHub, its whole history was rewritten (git filter-repo). Names and addresses of the internal network that the lab tests ran in were replaced by neutral placeholders:
- host names, such as `backup01.corp.example` for the backup server and `fs01`, `DFS01/02`, `app01`;
- the domain `corp.example`, NetBIOS `CORP`;
- address ranges `10.16.1.x` and `10.26.x.x`;
- the cluster `rke2-infra`.

The author and committer address of every commit became the GitHub no-reply address of the repository's owner.

Every commit got a new id. Older notes in the runbook, specs and reports cite build commits by their old ids ("`6a1d25d`"); this table maps them to the new ones.

| Old | New |
|---|---|
| `0754141` | `a2e361a` |
| `08745ec` | `b9861dc` |
| `09eea48` | `c728c62` |
| `0ae9660` | `5b5fad2` |
| `0dce75a` | `be45f97` |
| `11fc09e` | `55dddf3` |
| `1684b68` | `f707b20` |
| `188fc7f` | `3e5905d` |
| `19b4566` | `b5f45e5` |
| `1cd0eb8` | `f78d02a` |
| `1d87c44` | `6b1d134` |
| `1eb47eb` | `5d81a69` |
| `2aa0f73` | `aa6b14b` |
| `325bb8b` | `1cd225a` |
| `33bc662` | `5c202b2` |
| `34f5b42` | `e73f515` |
| `3e04259` | `444323e` |
| `4394bc8` | `8f9ae5a` |
| `44c51cb` | `c64dbdc` |
| `455eb0d` | `7068f4a` |
| `4b1f2c3` | `ad5a18a` |
| `4fd36b3` | `6c30b29` |
| `53cb200` | `159a724` |
| `5859829` | `d619e45` |
| `5ce43f0` | `2b056bd` |
| `5d042fa` | `b06983c` |
| `5de3e8e` | `860f241` |
| `601dca0` | `3a1c1e2` |
| `61f887e` | `35932d7` |
| `661f33f` | `110a318` |
| `66444e7` | `07c5bf4` |
| `6694c12` | `87515b4` |
| `67c2e82` | `b9ed0d8` |
| `68218ef` | `6386e54` |
| `68680a7` | `6df8634` |
| `692aaff` | `a771823` |
| `6a1d25d` | `25e1ebc` |
| `6b0d195` | `0775f31` |
| `75ac9cc` | `09ebea1` |
| `764d36c` | `b98b0d5` |
| `78836ac` | `0192d54` |
| `7fbf099` | `bca9784` |
| `81376fc` | `cb9e37d` |
| `82770d0` | `c028c55` |
| `82e7db2` | `b370de5` |
| `837e28b` | `60188c8` |
| `8aa2132` | `19e56b4` |
| `9265344` | `f09581e` |
| `976706b` | `2242f0e` |
| `98708b8` | `64bb28d` |
| `999aaf9` | `2d0f35f` |
| `9b0f875` | `7b8bf62` |
| `9cb3f4b` | `5b24858` |
| `9fe21b4` | `8dcf173` |
| `a6c453b` | `97ddb87` |
| `a947f56` | `3edf01d` |
| `aef78fb` | `363d036` |
| `b1846ab` | `8b99660` |
| `bf51cc2` | `5995afc` |
| `c1b5244` | `62e5ad3` |
| `c308e47` | `5349c70` |
| `d8cbb48` | `66b125f` |
| `da0bc62` | `60866db` |
| `df1b4cb` | `35978e3` |
| `e02cab3` | `f1a02d0` |
| `e2170c9` | `c4e86ad` |
| `e2f95cf` | `bb9ce16` |
| `e48255e` | `142ad2c` |
| `e5d4d55` | `495c691` |
| `e85486a` | `e7975fa` |
| `ea0bc67` | `1d8fb61` |
| `ecec970` | `ae565fc` |
| `ef406ee` | `29d369d` |
| `f5f9e32` | `2daea9e` |
| `f7f18f9` | `9c5d90f` |
| `f874bbd` | `8c70879` |
| `f9a280d` | `8df05c5` |
| `ff73448` | `e0cf4af` |

# Changelog

## [0.6.0](https://github.com/medokin/ReservePane/compare/v0.5.0...v0.6.0) (2026-09-17)


### ⚠ BREAKING CHANGES

* **opencode:** follow the active workspace automatically

### Features

* **opencode:** follow the active workspace automatically ([89cc076](https://github.com/medokin/ReservePane/commit/89cc07663aa79aca8d7afeb8f785210224e8ac9f))


### Bug Fixes

* **claude:** reduce usage polling frequency ([#55](https://github.com/medokin/ReservePane/issues/55)) ([5edffc0](https://github.com/medokin/ReservePane/commit/5edffc01bc5a7b81887ad82d9b3a0b5a075e5e43))
* **opencode:** correct budget and spend currency conversion ([7dd018b](https://github.com/medokin/ReservePane/commit/7dd018b6124740108adf55b1ab007b4cc9c4e8fe))

## [0.5.0](https://github.com/medokin/ReservePane/compare/v0.4.2...v0.5.0) (2026-09-12)


### Features

* **ollama:** show estimated spend and monthly budget ([#51](https://github.com/medokin/ReservePane/issues/51)) ([6295c3b](https://github.com/medokin/ReservePane/commit/6295c3b5b7f7e8ee5a47cb4d57dda2ee079f97bf))

## [0.4.2](https://github.com/medokin/ReservePane/compare/v0.4.1...v0.4.2) (2026-09-12)


### Bug Fixes

* **ollama:** display monthly usage percentages ([#49](https://github.com/medokin/ReservePane/issues/49)) ([e03faaf](https://github.com/medokin/ReservePane/commit/e03faaf9b241aea6f807ce862f94c0de64e5cbd8))

## [0.4.1](https://github.com/medokin/ReservePane/compare/v0.4.0...v0.4.1) (2026-09-12)


### Bug Fixes

* **ui:** show unavailable usage without a misleading meter ([#47](https://github.com/medokin/ReservePane/issues/47)) ([03a7d63](https://github.com/medokin/ReservePane/commit/03a7d6365b4fab6433ab48d10600908146c69607))

## [0.4.0](https://github.com/medokin/ReservePane/compare/v0.3.0...v0.4.0) (2026-09-11)


### Features

* **app:** add ollama cloud status and refresh controls ([#46](https://github.com/medokin/ReservePane/issues/46)) ([f01e630](https://github.com/medokin/ReservePane/commit/f01e630e35d7fd5cbb0f4af1371f741d215e5af9))
* **providers:** add grok quota monitoring ([#43](https://github.com/medokin/ReservePane/issues/43)) ([51e725c](https://github.com/medokin/ReservePane/commit/51e725c0de3692826ab9b0e03382bbf7f3463f59))


### Bug Fixes

* **providers:** retry opencode db reads when the database is busy ([#45](https://github.com/medokin/ReservePane/issues/45)) ([284251a](https://github.com/medokin/ReservePane/commit/284251a7a1bb2cac1ced1a85333ce4273565044d))

## [0.3.0](https://github.com/medokin/ReservePane/compare/v0.2.0...v0.3.0) (2026-08-28)


### Features

* **installer:** start app after interactive install ([#39](https://github.com/medokin/ReservePane/issues/39)) ([388c330](https://github.com/medokin/ReservePane/commit/388c3307e98e97d1c0e88fed40d397542d50a44d))
* **ui:** show startup tray notification ([#41](https://github.com/medokin/ReservePane/issues/41)) ([b72ca80](https://github.com/medokin/ReservePane/commit/b72ca80cc3414ec9ee4c9a0e731918fe16c28a88))

## [0.2.0](https://github.com/medokin/ReservePane/compare/v0.1.2...v0.2.0) (2026-08-28)


### Features

* **branding:** rename quotaglass to reservepane ([20ab1bd](https://github.com/medokin/ReservePane/commit/20ab1bdb3c6076594ae57e2f0a910183ee7110ac))

## [0.1.2](https://github.com/medokin/QuotaGlass/compare/v0.1.1...v0.1.2) (2026-08-28)


### Bug Fixes

* **claude:** refresh expired access tokens ([#32](https://github.com/medokin/QuotaGlass/issues/32)) ([fa762f7](https://github.com/medokin/QuotaGlass/commit/fa762f7319bb18d47f54fd9cf9676643bf11b11a))

## [0.1.1](https://github.com/medokin/QuotaGlass/compare/v0.1.0...v0.1.1) (2026-08-27)


### Bug Fixes

* **release:** allow draft asset reconciliation ([#28](https://github.com/medokin/QuotaGlass/issues/28)) ([c6a461e](https://github.com/medokin/QuotaGlass/commit/c6a461eee9c9104d94968f176e193d93aa37e944))
* **release:** restore runtime pack before publish ([#26](https://github.com/medokin/QuotaGlass/issues/26)) ([47dac5c](https://github.com/medokin/QuotaGlass/commit/47dac5c02c87ed8f44cbcd183e19580375bac51a))

## [0.1.0](https://github.com/medokin/QuotaGlass/compare/v0.0.1...v0.1.0) (2026-08-27)


### Features

* **installer:** add per-user windows installer ([#23](https://github.com/medokin/QuotaGlass/issues/23)) ([d73d05c](https://github.com/medokin/QuotaGlass/commit/d73d05ce361b33846f2f279c7d5ca8a0c0012678))
* **providers:** add opencode company seat member budget monitoring ([088eb3c](https://github.com/medokin/QuotaGlass/commit/088eb3c723291a0bceb2df3ee09d238aec3f40a9))
* **providers:** add opencode go quota monitoring ([#15](https://github.com/medokin/QuotaGlass/issues/15)) ([91ab4f4](https://github.com/medokin/QuotaGlass/commit/91ab4f4611fed22798e2fc3823ba9b7ab4d191bb))
* **providers:** support opencode console sessions and multiple workspaces ([14bc75c](https://github.com/medokin/QuotaGlass/commit/14bc75cc8a8994c77a527683201c93454a0becc2))
* **release:** enforce release commit inputs ([0cbc32e](https://github.com/medokin/QuotaGlass/commit/0cbc32e3c45d001277edac3cde315e5ac74d1144))


### Bug Fixes

* **ci:** suppress duplicate release pr runs ([0173233](https://github.com/medokin/QuotaGlass/commit/0173233240d0c26785c8a649a2f491bcdca906c6))
* **release:** queue checks for generated prs ([7b48af5](https://github.com/medokin/QuotaGlass/commit/7b48af5bc3ec39d6c8335a5c61862a5a5c15f1e1))
* **release:** record dispatched checks on prs ([b0669b3](https://github.com/medokin/QuotaGlass/commit/b0669b367bd733fca21d21c99f534d558aaa231b))

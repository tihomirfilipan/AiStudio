# Upstream

Ovaj repozitorij sadrži izvorni kod projekta **AgOpenGPS** kao bazu za vlastite izmjene.

- Izvor: https://github.com/AgOpenGPS-Official/AgOpenGPS
- Uvezeni commit: `64984a3d36e0328c3c7e0b0f7641bf8d3f68fc0a` (23. 10. 2026.)
- Licenca: Apache License 2.0 (vidi `LICENSE`)

Za povlačenje novijih promjena iz izvornog projekta:

```bash
git remote add upstream https://github.com/AgOpenGPS-Official/AgOpenGPS.git
git fetch upstream
# usporedi/preuzmi promjene po potrebi, npr.:
git diff HEAD upstream/master --stat
```

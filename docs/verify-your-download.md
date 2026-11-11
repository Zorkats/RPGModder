# Check your RPGModder download

Every release lists the SHA-256 hash of each package in a file named `SHA256SUMS`. If the hash of your download equals its line there, you have the file the release built.

## Windows

1. Download your package and `SHA256SUMS` from the [Releases page](https://github.com/Zorkats/RPGModder/releases).
2. In PowerShell, in the download folder:

   ```powershell
   Get-FileHash .\RPGModder-<version>-win-x64.zip -Algorithm SHA256
   ```

3. Compare the `Hash` value with the line for that file in `SHA256SUMS`. Letter case does not matter.

## Linux

In the download folder:

```bash
sha256sum -c --ignore-missing SHA256SUMS
```

Your package must show `OK`.

## Windows SmartScreen warning

RPGModder is not code signed, so the first start may show "Windows protected your PC". After the hash check above passes, choose **More info**, then **Run anyway**.

## Updates inside RPGModder

The built-in updater does this check itself. It downloads `SHA256SUMS` with the package and refuses to install a package whose hash does not match.

# Keys Directory

This directory holds the RSA key pair used to sign license tokens.

| File           | Purpose                                      | Commit? |
|----------------|----------------------------------------------|---------|
| `signing.key`  | RSA-4096 **private key** (PKCS#8 PEM)        | **NEVER** |
| `signing.pub`  | RSA-4096 **public key** (SPKI PEM)           | Optional |

`signing.key` is excluded from git via `.gitignore`.
Never copy it off the server or store it in a secrets manager that grants read
access to the app process — the app only needs the public key.

## Generating keys

From the solution root (or `DM.LicenseServer/`):

```
dotnet run --project tools/GenerateKeys
```

The tool writes both files here, prints the public key to stdout, and refuses to
overwrite an existing `signing.key` without `--force`.

## Embedding the public key in DM.App

Copy the contents of `signing.pub` into a string constant in
`DM.App/Services/LicenseValidator.cs` (or equivalent).  
The app uses this to verify the JWT signature — it never needs the private key.

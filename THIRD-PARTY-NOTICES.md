# Third-party notices

Bastion is licensed under the PolyForm Noncommercial License 1.0.0 (see `LICENSE`).
The components below are distributed with Bastion's release builds under their own
licenses, reproduced here as those licenses require.

## Redistributed in release builds

### CommunityToolkit.Mvvm 8.4.2

Copyright © .NET Foundation and Contributors. All rights reserved.
Project: https://github.com/CommunityToolkit/dotnet

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.

### Microsoft.Extensions.DependencyInjection 10.0.11 (and Microsoft.Extensions.DependencyInjection.Abstractions)

© Microsoft Corporation. All rights reserved.
Project: https://github.com/dotnet/runtime

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.

### .NET runtime and Windows Desktop runtime

Bastion targets .NET 10 (MIT, © .NET Foundation and Contributors). Framework-dependent
builds do not redistribute the runtime; self-contained builds include it under the MIT
license above.

## Used only for development and tests (not redistributed)

| Package | License | Purpose |
|---|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 | test framework |
| Microsoft.NET.Test.Sdk | MIT | test host |
| coverlet.collector | MIT | coverage |
| NSubstitute | BSD-3-Clause | test doubles |
| Konscious.Security.Cryptography.Argon2 | MIT | reference implementation for the Argon2 differential tests |

## Original implementations and assets

- Argon2 (RFC 9106) and BLAKE2b (RFC 7693) are implemented in `src/Bastion.Core/Crypto`
  from the RFCs; no third-party code is included. Test vectors are taken from the RFCs.
- The application icon, the Lamplight theme and all line-art assets are original work.
- The embedded common-password list (`src/Bastion.App/Services/CommonPasswords.cs`) was
  written for this project; no third-party list was copied.
- Fonts (Segoe UI Variable, Segoe Fluent Icons, Segoe MDL2 Assets, Cascadia Mono) are
  referenced from the user's Windows installation and are not redistributed.

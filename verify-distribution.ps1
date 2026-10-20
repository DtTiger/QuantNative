param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
$sig = Get-AuthenticodeSignature -LiteralPath $Exe
if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate) {
    throw "Distribution blocked: Authenticode status is $($sig.Status). Obtain a trusted signing identity or publish an MSIX through Microsoft Store."
}
if (-not $sig.TimeStamperCertificate) { throw 'Distribution blocked: no trusted timestamp found.' }
[pscustomobject]@{
    Status = $sig.Status
    Publisher = $sig.SignerCertificate.Subject
    SHA256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash
    Note = 'Valid signature is necessary for this release gate; it does not guarantee SmartScreen reputation or override enterprise policy.'
}

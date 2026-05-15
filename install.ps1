# Run as Administrator
$msix = "$PSScriptRoot\src\DouyinTTS.App\AppPackages\DouyinTTS.App_1.0.0.0_x64_Test\DouyinTTS.App_1.0.0.0_x64.msix"

# Enable sideloading
$regPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx'
Set-ItemProperty -Path $regPath -Name 'AllowAllTrustedApps' -Value 1

# Install certificate
$cert = "$PSScriptRoot\src\DouyinTTS.App\DouyinTTS.cer"
Import-Certificate -FilePath $cert -CertStoreLocation 'Cert:\LocalMachine\Root'
Import-Certificate -FilePath $cert -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'

# Install MSIX
Add-AppxPackage -Path $msix -AllowUnsigned

Write-Host "Done! DouyinTTS installed."

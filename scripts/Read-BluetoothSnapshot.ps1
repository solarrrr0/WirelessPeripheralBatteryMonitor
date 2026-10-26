#requires -Version 5.1
# Run with Windows PowerShell 5.1: its built-in WinRT projection requires no SDK.
[CmdletBinding()]
param(
    [string]$InventoryPath,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if(-not $InventoryPath){$InventoryPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'device_inventory.local.json'}
if(-not $OutputPath){$OutputPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'bluetooth_snapshot.local.json'}
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$classicType=[Windows.Devices.Bluetooth.BluetoothDevice,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
$leType=[Windows.Devices.Bluetooth.BluetoothLEDevice,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
$infoType=[Windows.Devices.Enumeration.DeviceInformation,Windows.Devices.Enumeration,ContentType=WindowsRuntime]
$serviceResultType=[Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
$characteristicResultType=[Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicsResult,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
$readResultType=[Windows.Devices.Bluetooth.GenericAttributeProfile.GattReadResult,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
$asTask=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Wait-Operation($operation,[type]$resultType) {
    $task=$asTask.MakeGenericMethod($resultType).Invoke($null,@($operation))
    if(-not $task.Wait(5000)) { try{$operation.Cancel()}catch{}; throw [TimeoutException]::new('WinRT query timed out') }
    return $task.Result
}
function Error-Summary($exception) { return ($exception.GetType().Name + ' HRESULT=' + $exception.HResult) }
try { $inventory=Get-Content -LiteralPath $InventoryPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw 'Inventory JSON could not be parsed; no raw identifiers were printed.' }
$roots=@($inventory.Devices | Where-Object {$_.Present -and $_.InstanceId -match '^(BTHENUM|BTHLE)\\DEV_[0-9A-F]{12}'})
$results=@(foreach($root in $roots) {
    $device=$null; $connected=$null; $failure=$null; $canonical=@(); $gatt=@(); $gattNote='Skipped: device is not confirmed connected.'
    $null=$root.InstanceId -match '^(BTHENUM|BTHLE)\\DEV_([0-9A-F]{12})'
    $isLE=$Matches[1] -eq 'BTHLE'; $address=[Convert]::ToUInt64($Matches[2],16)
    try {
        if($isLE) { $device=Wait-Operation ([Windows.Devices.Bluetooth.BluetoothLEDevice]::FromBluetoothAddressAsync($address)) $leType }
        else { $device=Wait-Operation ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($address)) $classicType }
        if($device) {
            $connected=([string]$device.ConnectionStatus -eq 'Connected')
            $keys=[string[]]@('System.Devices.BatteryLife','System.Devices.BatteryPlusCharging','System.Devices.BatteryPlusChargingText','System.Devices.Aep.IsConnected','System.Devices.Aep.IsPaired')
            foreach($kind in @('Device','DeviceContainer')) {
                try {
                    $identity=if($kind -eq 'Device'){$root.InstanceId}else{[string]$root.ContainerId}
                    $enumKind=[Windows.Devices.Enumeration.DeviceInformationKind]::$kind
                    $info=Wait-Operation ([Windows.Devices.Enumeration.DeviceInformation]::CreateFromIdAsync($identity,$keys,$enumKind)) $infoType
                    if($info) {
                        # The PowerShell 5.1 WinRT map is an enumerable COM object,
                        # not a PowerShell dictionary. Enumerate key/value pairs.
                        $values=@{}
                        foreach($entry in $info.Properties){$values[[string]$entry.Key]=$entry.Value}
                        foreach($key in $keys) {
                            $canonical+= [pscustomobject]@{Kind=$kind;Key=$key;Present=$values.ContainsKey($key);Value=$values[$key]}
                        }
                    }
                } catch { $canonical+=[pscustomobject]@{Kind=$kind;Error=(Error-Summary $_.Exception)} }
            }
            if($isLE -and $connected) {
                $gattNote='Standard BAS only; discovery and read, no pairing, writes or notification subscription.'
                $services=Wait-Operation ($device.GetGattServicesForUuidAsync([guid]'0000180f-0000-1000-8000-00805f9b34fb',[Windows.Devices.Bluetooth.BluetoothCacheMode]::Cached)) $serviceResultType
                $gatt+=[pscustomobject]@{Step='BatteryService';Status=[string]$services.Status;Count=$services.Services.Count}
                foreach($service in $services.Services) { try {
                    $chars=Wait-Operation ($service.GetCharacteristicsForUuidAsync([guid]'00002a19-0000-1000-8000-00805f9b34fb',[Windows.Devices.Bluetooth.BluetoothCacheMode]::Cached)) $characteristicResultType
                    $gatt+=[pscustomobject]@{Step='BatteryLevelCharacteristic';Status=[string]$chars.Status;Count=$chars.Characteristics.Count}
                    foreach($characteristic in $chars.Characteristics) {
                        if(($characteristic.CharacteristicProperties -band [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties]::Read) -eq 0){continue}
                        $reading=Wait-Operation ($characteristic.ReadValueAsync([Windows.Devices.Bluetooth.BluetoothCacheMode]::Uncached)) $readResultType
                        $percent=$null
                        if([string]$reading.Status -eq 'Success' -and $reading.Value.Length -eq 1) {
                            $reader=[Windows.Storage.Streams.DataReader,Windows.Storage.Streams,ContentType=WindowsRuntime]::FromBuffer($reading.Value)
                            try { $value=[int]$reader.ReadByte(); if($value -le 100){$percent=$value} } finally {$reader.Dispose()}
                        }
                        $gatt+=[pscustomobject]@{Step='BatteryLevelRead';Status=[string]$reading.Status;Percent=$percent;ReadAt=[DateTimeOffset]::Now.ToString('o')}
                    }
                } finally {$service.Dispose()} }
            }
        }
    } catch { $failure=Error-Summary $_.Exception } finally { if($device){$device.Dispose()} }
    [pscustomobject]@{Name=$root.Name;Transport=if($isLE){'BLE'}else{'Classic'};Connected=$connected;Error=$failure;CanonicalProperties=$canonical;GattNote=$gattNote;Gatt=$gatt}
})
$snapshot=[ordered]@{CollectedAt=[DateTimeOffset]::Now.ToString('o');Safety='Known paired roots only; no radio scan, pairing, HID reports or GATT writes.';Devices=$results}
$snapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$snapshot | ConvertTo-Json -Depth 10

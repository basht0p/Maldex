rule EICAR_Test_File : malicious test
{
    meta:
        description = "The standard EICAR antivirus test file"
    strings:
        $eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
    condition:
        $eicar
}

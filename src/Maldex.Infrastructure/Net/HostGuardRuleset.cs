namespace Maldex.Infrastructure.Net;

/// <summary>
/// The host-namespace guard: the host never routes for, or accepts traffic from, per-run <c>mx*</c> interfaces,
/// and run bridges carry only IPv4 and ARP. Must equal <c>deploy/host/maldex-host-guard.nft</c>.
/// </summary>
public static class HostGuardRuleset
{
    public static string Build() =>
        """
        #!/usr/sbin/nft -f
        # Maldex host guard: the host never routes for, or accepts traffic from, per-run mx* interfaces,
        # and run bridges carry only IPv4 and ARP frames.
        table inet maldex_guard
        delete table inet maldex_guard
        table inet maldex_guard {
        	chain forward {
        		type filter hook forward priority -10; policy accept;
        		iifname "mx*" drop
        		oifname "mx*" drop
        	}
        	chain input {
        		type filter hook input priority -10; policy accept;
        		iifname "mx*" drop
        	}
        }
        table bridge maldex_guard
        delete table bridge maldex_guard
        table bridge maldex_guard {
        	chain forward {
        		type filter hook forward priority -10; policy accept;
        		ibrname "mxb*" ether type != { ip, arp } drop
        	}
        	chain input {
        		type filter hook input priority -10; policy accept;
        		ibrname "mxb*" drop
        	}
        }

        """.ReplaceLineEndings("\n");
}

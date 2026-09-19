

import { Folder, FolderPlus, PenTool, SquareTerminal } from "lucide-react"
import { useEffect, useState } from "react"
import { NavMain } from "@/components/nav-main"
import { NavUser } from "@/components/nav-user"
import { SidebarLogo } from "@/components/sidebar-logo"
import {
    Sidebar,
    SidebarContent,
    SidebarFooter,
    SidebarHeader,
    SidebarRail,
} from "@/components/ui/sidebar"
import { useAuth } from "@/lib/auth"
import { apiUrl, authFetch } from "@/lib/config"

// User types with an uploadable profile photo (Accounts/Me/Avatar) — PMIS
// employees (user_type 0) instead use the PGAS HRIS photo URL below.
const AVATAR_USER_TYPES = new Set([1, 2])

export function AppSidebar({ ...props }: React.ComponentProps<typeof Sidebar>) {
    const { user, avatarUrl } = useAuth()
    const [hasPrincipals, setHasPrincipals] = useState(false)

    // Gates the "For Signature (Alternate)" item — only shown once someone
    // else has actually set this user as their active alternate, same
    // "fetch a gate flag, conditionally render" convention as check_admin.
    useEffect(() => {
        let cancelled = false
        authFetch(apiUrl("AlternateSignatories/my_principals"))
            .then((res) => (res.ok ? res.json() : []))
            .then((data: unknown[]) => { if (!cancelled) setHasPrincipals(Array.isArray(data) && data.length > 0) })
            .catch(() => { if (!cancelled) setHasPrincipals(false) })
        return () => { cancelled = true }
    }, [])

    const navMain = [
        {
            title: "Dashboard",
            url: "/",
            icon: SquareTerminal,
            isActive: true,
        },
        {
            title: "Upload Document",
            url: "/documents/uploaded",
            icon: FolderPlus,
            isActive: true,
        },
        {
            title: "Signature",
            url: "#",
            icon: PenTool,
            isActive: true,
            items: [
                { title: "For Signature", url: "/signature/ForSignature" },
                ...(hasPrincipals ? [{ title: "For Signature (Alternate)", url: "/signature/ForSignatureAlternate" }] : []),
                { title: "My Signature", url: "/signature/MySignature" },
            ],
        },
        {
            title: "Documents",
            url: "/documents/mydocument",
            icon: Folder,
            isActive: true,
        },
        {
            title: "Admin",
            url: "#",
            icon: Folder,
            isActive: false,
            items: [
                { title: "For Approve", url: "/admin/certificate-requests" },
                { title: "Reconstruction", url: "/admin/certificate-requests" },
                { title: "Active Certificates", url: "/admin/active-certificates" },
                { title: "Document Search", url: "/admin/document-search" },
            ],
        },
    ]

    // Only show Settings if it has sub-items
    const filteredNav = navMain.filter(
        (item) => !item.items || item.items.length > 0
    )

    return (
        <Sidebar collapsible="icon" {...props}>
            <SidebarHeader className="bg-primary/10 border-b border-primary/20">
                <SidebarLogo />
            </SidebarHeader>
            <SidebarContent>
                <NavMain items={filteredNav} />
            </SidebarContent>
            <SidebarFooter>
                <NavUser
                    user={{
                        name: user?.fullName ?? "User",
                        email: user?.email ?? "",
                        avatar: user
                            ? AVATAR_USER_TYPES.has(user.user_type)
                                ? (avatarUrl ?? "")
                                : `https://pgas.ph/hris/Content/images/photos/${user.eid}.png`
                            : "",
                    }}
                />
            </SidebarFooter>
            <SidebarRail />
        </Sidebar>
    )
}

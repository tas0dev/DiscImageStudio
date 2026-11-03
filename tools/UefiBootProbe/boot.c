/* A freestanding x86-64 UEFI application for the optical boot smoke test.
 * QEMU's debug console receives a marker; isa-debug-exit then ends the VM.
 */
typedef unsigned long long EfiStatus;

__attribute__((ms_abi)) EfiStatus efi_main(void *image, void *system_table)
{
    (void)image;
    (void)system_table;
    const char *message = "DiscImageStudio UEFI boot OK\n";
    while (*message)
        __asm__ volatile ("outb %0, %1" : : "a"(*message++), "Nd"((unsigned short)0xE9));
    __asm__ volatile ("outl %0, %1" : : "a"(0x10u), "Nd"((unsigned short)0xF4));
    return 0;
}

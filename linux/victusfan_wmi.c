// Raw HP BIOS WMI passthrough (same call as hp-wmi / VictusFan.exe). /dev/victusfan:
// write "<cmdtype hex> [data bytes hex]..." then read back "<rc> <data hex...>".
#include <linux/module.h>
#include <linux/miscdevice.h>
#include <linux/fs.h>
#include <linux/wmi.h>
#include <linux/acpi.h>
#include <linux/slab.h>
#include <linux/mutex.h>
#include <linux/uaccess.h>

#define GUID "5FB7F034-2C63-45e9-BE91-3D44E2C707E4"

struct args { u8 sig[4]; u32 command; u32 type; u32 size; u8 data[128]; } __packed;

static DEFINE_MUTEX(lock);
static char reply[600];

static ssize_t vf_write(struct file *f, const char __user *ubuf, size_t n, loff_t *off)
{
	char line[256], *p, *tok;
	struct args a = { .sig = "SECU", .command = 0x20008 };
	struct acpi_buffer in = { sizeof(a), &a }, out = { ACPI_ALLOCATE_BUFFER, NULL };
	union acpi_object *o;
	unsigned int v, i = 0;
	bool first = true;
	int len = 0, rc = -1;

	if (n >= sizeof(line) || copy_from_user(line, ubuf, n))
		return -EINVAL;
	line[n] = 0;
	for (p = line; (tok = strsep(&p, " \n")); ) {
		if (!*tok)
			continue;
		if (kstrtouint(tok, 16, &v) || v > 0xffffffff)
			return -EINVAL;
		if (first) { a.type = v; first = false; }
		else if (i < sizeof(a.data)) a.data[i++] = v;
		else return -EINVAL;
	}
	if (first)
		return -EINVAL;
	a.size = i < 4 ? 4 : i; // BIOS expects at least 4 bytes (zero padded)

	mutex_lock(&lock);
	if (ACPI_FAILURE(wmi_evaluate_method(GUID, 0, 3, &in, &out))) {
		len = scnprintf(reply, sizeof(reply), "-1\n");
	} else {
		o = out.pointer;
		if (o && o->type == ACPI_TYPE_BUFFER && o->buffer.length >= 8) {
			rc = ((u32 *)o->buffer.pointer)[1];
			len = scnprintf(reply, sizeof(reply), "%d", rc);
			for (i = 8; i < o->buffer.length && i < 8 + 128; i++)
				len += scnprintf(reply + len, sizeof(reply) - len, " %02x", o->buffer.pointer[i]);
			reply[len++] = '\n';
		} else {
			len = scnprintf(reply, sizeof(reply), "-1\n");
		}
		kfree(out.pointer);
	}
	reply[len] = 0;
	mutex_unlock(&lock);
	return n;
}

static ssize_t vf_read(struct file *f, char __user *ubuf, size_t n, loff_t *off)
{
	ssize_t r;
	mutex_lock(&lock);
	r = simple_read_from_buffer(ubuf, n, off, reply, strlen(reply));
	mutex_unlock(&lock);
	return r;
}

static const struct file_operations fops = { .owner = THIS_MODULE, .read = vf_read, .write = vf_write };
static struct miscdevice dev = { MISC_DYNAMIC_MINOR, "victusfan", &fops, .mode = 0600 };

static int __init vf_init(void)
{
	if (!wmi_has_guid(GUID))
		return -ENODEV;
	return misc_register(&dev);
}
static void __exit vf_exit(void) { misc_deregister(&dev); }

module_init(vf_init);
module_exit(vf_exit);
MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("HP BIOS WMI passthrough for VictusFan");

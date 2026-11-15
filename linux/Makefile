obj-m := victusfan_wmi.o
KVER ?= $(or $(KERNELRELEASE),$(shell uname -r))
all:
	make -C /lib/modules/$(KVER)/build M=$(PWD) modules
clean:
	make -C /lib/modules/$(KVER)/build M=$(PWD) clean

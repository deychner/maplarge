shopping_list = [
    "Apples",
    "Bananas", 
    "Bread",
    "Milk",
    "Eggs",
    "Cheese"
]

def print_list():
    print("Shopping List:")
    for i, item in enumerate(shopping_list, 1):
        print(f"{i}. {item}")

if __name__ == "__main__":
    print_list()